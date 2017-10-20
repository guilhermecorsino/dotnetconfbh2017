﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using RazorRenderer;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Primitives;
using System.Text.RegularExpressions;

namespace Blazor.Sdk.Host
{
    internal static class RazorCompilation
    {
        static PathString binDir = new PathString("/_bin");
        static IDictionary<string, (byte[], byte[])> cachedCompilationResults = new Dictionary<string, (byte[], byte[])>();
        static object cachedCompilationResultsLock = new object();
        static FileSystemWatcher activeFileSystemWatcher; // If we don't hold a reference to this, it gets disposed automatically on Linux (though not on Windows)

        public static void UseRazorCompilation(this IApplicationBuilder builder, string rootDir)
        {
            BeginFileSystemWatcher(rootDir);

            builder.Use(async (context, next) =>
            {
                var req = context.Request;
                if (req.Path.StartsWithSegments(binDir)
                    && req.Query["type"] == "razorviews"
                    && req.Path.Value.EndsWith(".dll"))
                {
                    var compiledAssembly = GetCompiledViewsAssembly(rootDir, context.Request);
                    context.Response.ContentType = "application/octet-steam";
                    await context.Response.Body.WriteAsync(compiledAssembly.Item1, 0, compiledAssembly.Item1.Length);
                }
                else
                {
                    await next();
                }
            });
        }

        public static (byte[], byte[]) GetCachedCompiledAssembly(string assemblyName)
        {
            lock(cachedCompilationResultsLock)
            {
                var cacheKey = assemblyName;
                return cachedCompilationResults.TryGetValue(cacheKey, out var result)
                    ? result
                    : (null, null);
            }
        }

        private static void BeginFileSystemWatcher(string rootDir)
        {
            activeFileSystemWatcher = new FileSystemWatcher
            {
                Path = rootDir,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = true
            };

            activeFileSystemWatcher.Deleted += (sender, evtArgs) => OnFileSystemWatcherEvent(evtArgs);
            activeFileSystemWatcher.Created += (sender, evtArgs) => OnFileSystemWatcherEvent(evtArgs);
            activeFileSystemWatcher.Changed += (sender, evtArgs) => OnFileSystemWatcherEvent(evtArgs);
            activeFileSystemWatcher.Renamed += (sender, evtArgs) => OnFileSystemWatcherEvent(evtArgs);

            activeFileSystemWatcher.EnableRaisingEvents = true;
        }

        private static void OnFileSystemWatcherEvent(FileSystemEventArgs evtArgs)
        {
            // We only care about .cshtml files
            if (Path.GetExtension(evtArgs.FullPath) == ".cshtml")
            {
                lock (cachedCompilationResultsLock)
                {
                    cachedCompilationResults.Clear();
                }

                LiveReloading.RequestReload();
            }
        }

        internal static (byte[], byte[]) GetCompiledViewsAssembly(string rootDir, HttpRequest forRequest)
        {
            // Determine the desired views assembly name based on the URL
            var requestPath = forRequest.Path.Value;
            var assemblyFilename = Path.ChangeExtension(requestPath.Substring(requestPath.LastIndexOf('/') + 1), "dll");
            var references = forRequest.Query["reference"];
            return GetCompiledViewsAssembly(rootDir, assemblyFilename, references);
        }

        internal static (byte[], byte[]) GetCompiledViewsAssembly(string rootDir, string assemblyFilename, IEnumerable<string> references)
        {
            // Get or create cached compilation result. Doesn't really matter that we might be blocking
            // other request threads with this lock, as this is a development-time feature only.
            (byte[], byte[]) compiledAssembly;
            lock (cachedCompilationResultsLock)
            {
                var cacheKey = assemblyFilename;
                if (!cachedCompilationResults.ContainsKey(cacheKey))
                {
                    cachedCompilationResults[cacheKey] = PerformCompilation(assemblyFilename, rootDir, references);
                }

                compiledAssembly = cachedCompilationResults[cacheKey];
            }

            return compiledAssembly;
        }

        private static (byte[], byte[]) PerformCompilation(string assemblyFilename, string rootDir, IEnumerable<string> additionalReferenceAssemblies)
        {
            // Get the total list of assembly paths to reference during compilation
            var inferredMainAssemblyFilename = InferMainAssemblyFilename(assemblyFilename);
            var referenceAssemblyFilenames = new List<string>();
            if (!string.IsNullOrEmpty(inferredMainAssemblyFilename))
            {
                referenceAssemblyFilenames.Add(inferredMainAssemblyFilename);
            }
            referenceAssemblyFilenames.AddRange(additionalReferenceAssemblies);

            using (var ms = new MemoryStream())
            using (var symbols = new MemoryStream())
            {
                RazorVDomCompiler.CompileToStream(
                    enableLogging: false,
                    rootDir: rootDir,
                    referenceAssemblies: referenceAssemblyFilenames.Select(filename =>
                    {
                        var path = Path.Combine(rootDir, "bin", "Debug", "netcoreapp1.0", filename);

                        if (!File.Exists(path))
                        {
                            path = Path.Combine(rootDir, filename);
                        }

                        return path;
                    }).ToArray(),
                    outputAssemblyName: Path.GetFileNameWithoutExtension(assemblyFilename),
                    outputStream: ms,
                    pdbStream: symbols);

                return (ms.ToArray(), symbols.ToArray());
            }
        }

        private static string InferMainAssemblyFilename(string viewsAssemblyFilename)
        {
            var partBeforeSuffix = Regex.Match(viewsAssemblyFilename, "(.*)(\\.\\d+)\\.Views\\.dll$");
            if (partBeforeSuffix.Success)
            {
                return $"{partBeforeSuffix.Groups[1].Value}.dll";
            }

            partBeforeSuffix = Regex.Match(viewsAssemblyFilename, "(.*)\\.Views\\.dll$");
            if (partBeforeSuffix.Success)
            {
                return $"{partBeforeSuffix.Groups[1].Value}.dll";
            }


            return null;
        }
    }
}
