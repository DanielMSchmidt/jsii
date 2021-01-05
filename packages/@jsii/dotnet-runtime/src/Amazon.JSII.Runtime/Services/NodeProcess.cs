using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Amazon.JSII.Runtime.Services
{
    internal sealed class NodeProcess : INodeProcess
    {
        readonly Process _process;
        private const string JsiiRuntime = "JSII_RUNTIME";
        private const string JsiiDebug = "JSII_DEBUG";
        private const string JsiiAgent = "JSII_AGENT";
        private const string JsiiAgentVersionString = "DotNet/{0}/{1}/{2}";

        public NodeProcess(IJsiiRuntimeProvider jsiiRuntimeProvider, ILoggerFactory loggerFactory)
        {
            loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            var logger = loggerFactory.CreateLogger<NodeProcess>();

            var runtimePath = Environment.GetEnvironmentVariable(JsiiRuntime);
            if (string.IsNullOrWhiteSpace(runtimePath))
                runtimePath = jsiiRuntimeProvider.JsiiRuntimePath;

            var utf8 = new UTF8Encoding(false /* no BOM */);
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    ArgumentList = { "--max-old-space-size=4096", runtimePath },
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    StandardInputEncoding = utf8,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = utf8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = utf8
                }
            };

            var assemblyVersion = GetAssemblyFileVersion();
            _process.StartInfo.EnvironmentVariables.Add(JsiiAgent,
                string.Format(CultureInfo.InvariantCulture, JsiiAgentVersionString, Environment.Version,
                    assemblyVersion.Item1, assemblyVersion.Item2));

            var debug = Environment.GetEnvironmentVariable(JsiiDebug);
            if (!string.IsNullOrWhiteSpace(debug) && !_process.StartInfo.EnvironmentVariables.ContainsKey(JsiiDebug))
                _process.StartInfo.EnvironmentVariables.Add(JsiiDebug, debug);

            logger.LogDebug("Starting jsii runtime...");
            logger.LogDebug($"{_process.StartInfo.FileName} {_process.StartInfo.Arguments}");

            // Registering shutdown hook to have JS process gracefully terminate.
            AppDomain.CurrentDomain.ProcessExit += (snd, evt) => {
                try
                {
                    ((IDisposable)this).Dispose();
                }
                catch (Exception e)
                {
                    // If this throws, the app would crash ugly!
                    Console.Error.WriteLine($"Error cleaning up {nameof(NodeProcess)}: {e}");
                }
            };

            _process.Start();
        }

        public TextWriter StandardInput => _process.StandardInput;

        public TextReader StandardOutput => _process.StandardOutput;

        public TextReader StandardError => _process.StandardError;

        void IDisposable.Dispose()
        {
            if (_process.HasExited)
            {
                // Process already cleaned up, nothing to do!
                return;
            }

            StandardInput.Close();
            try
            {
                if (!_process.WaitForExit(5_000))
                {
                    // The process didn't exit in time... Let's kill it.
                    _process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process has already died, we're good!
            }
            catch (SystemException)
            {
                // The process has already died, we're good!
            }
            finally
            {
                _process.Dispose();
            }
        }

        /// <summary>
        /// Gets the target framework attribute value and
        /// the assembly file version for the current .NET assembly
        /// </summary>
        /// <returns>A tuple where Item1 is the target framework
        /// ie .NETCoreApp,Version=v2.1
        /// and item2 is the assembly file version (ie 1.0.0.0)</returns>
        private static Tuple<string, string> GetAssemblyFileVersion()
        {
            var assembly = typeof(NodeProcess).GetTypeInfo().Assembly;
            var assemblyFileVersionAttribute = assembly.GetCustomAttribute(typeof(AssemblyFileVersionAttribute)) as AssemblyFileVersionAttribute;
            var frameworkAttribute = assembly.GetCustomAttribute(typeof(TargetFrameworkAttribute)) as TargetFrameworkAttribute;
            return new Tuple<string, string>(
                frameworkAttribute?.FrameworkName ?? "Unknown",
                assemblyFileVersionAttribute?.Version ?? "Unknown"
            );
        }
    }
}
