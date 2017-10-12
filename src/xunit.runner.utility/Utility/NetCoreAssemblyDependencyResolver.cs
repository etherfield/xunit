#if NETCOREAPP1_0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.DotNet.InternalAbstractions;
using Microsoft.Extensions.DependencyModel;
using Xunit.Abstractions;

namespace Xunit
{
    /// <summary>
    /// A class which encapsulates support for resolving dependencies for .NET core assemblies.
    /// This includes support for using the .deps.json file that sits alongside the assembly
    /// so that dependencies do not need to be copied locally. It also supports loading native
    /// assembly assets.
    /// </summary>
    public class NetCoreAssemblyDependencyResolver : AssemblyLoadContext, IDisposable
    {
        readonly string assemblyFolder;
        readonly XunitPackageCompilationAssemblyResolver assemblyResolver;
        readonly DependencyContext dependencyContext;
        readonly IMessageSink internalDiagnosticsMessageSink;
        readonly Dictionary<string, Tuple<RuntimeLibrary, RuntimeAssetGroup>> managedAssemblyMap;
        readonly Dictionary<string, Tuple<RuntimeLibrary, RuntimeAssetGroup>> unmanagedAssemblyMap;

        /// <summary/>
        [Obsolete("Please call the constructor with the support for internal diagnostics messages")]
        public NetCoreAssemblyDependencyResolver(string assemblyFilePath) : this(assemblyFilePath, null) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="NetCoreAssemblyDependencyResolver"/> class.
        /// </summary>
        /// <param name="assemblyFilePath">The path to the assembly</param>
        /// <param name="internalDiagnosticsMessageSink">An optional message sink for use with internal diagnostics messages;
        /// may pass <c>null</c> for no internal diagnostics messages</param>
        public NetCoreAssemblyDependencyResolver(string assemblyFilePath, IMessageSink internalDiagnosticsMessageSink)
        {
            this.internalDiagnosticsMessageSink = internalDiagnosticsMessageSink;

            var assembly = LoadFromAssemblyPath(assemblyFilePath);

            assemblyFolder = Path.GetDirectoryName(assemblyFilePath);
            assemblyResolver = new XunitPackageCompilationAssemblyResolver();
            dependencyContext = DependencyContext.Load(assembly);

            var compatibleRuntimes = default(HashSet<string>);
            var currentRuntime = RuntimeEnvironment.GetRuntimeIdentifier();
            var fallbacks = dependencyContext.RuntimeGraph.FirstOrDefault(x => string.Equals(x.Runtime, currentRuntime, StringComparison.OrdinalIgnoreCase));
            if (fallbacks != null)
                compatibleRuntimes = new HashSet<string>(fallbacks.Fallbacks, StringComparer.OrdinalIgnoreCase);
            else
                compatibleRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            compatibleRuntimes.Add(currentRuntime);
            compatibleRuntimes.Add(string.Empty);

            managedAssemblyMap =
                dependencyContext.RuntimeLibraries
                                 .Where(lib => lib.RuntimeAssemblyGroups?.Count > 0)
                                 .Select(lib => Tuple.Create(lib, lib.RuntimeAssemblyGroups.FirstOrDefault(libGroup => compatibleRuntimes.Contains(libGroup.Runtime))))
                                 .Where(tuple => tuple.Item2?.AssetPaths != null)
                                 .SelectMany(tuple => tuple.Item2.AssetPaths.Where(x => x != null).Select(path => Tuple.Create(Path.GetFileNameWithoutExtension(path), Tuple.Create(tuple.Item1, tuple.Item2))))
                                 .ToDictionaryIgnoringDuplicateKeys(tuple => tuple.Item1, tuple => tuple.Item2, StringComparer.OrdinalIgnoreCase);

            if (internalDiagnosticsMessageSink != null)
                internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver..ctor] Managed assembly map includes: {string.Join(",", managedAssemblyMap.Keys.Select(k => $"'{k}'").OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}]"));

            unmanagedAssemblyMap =
                dependencyContext.RuntimeLibraries
                                 .Where(lib => lib.NativeLibraryGroups?.Count > 0)
                                 .Select(lib => Tuple.Create(lib, lib.NativeLibraryGroups.FirstOrDefault(libGroup => compatibleRuntimes.Contains(libGroup.Runtime))))
                                 .Where(tuple => tuple.Item2?.AssetPaths != null)
                                 .SelectMany(tuple => tuple.Item2.AssetPaths.Where(x => x != null).Select(path => Tuple.Create(Path.GetFileName(path), Tuple.Create(tuple.Item1, tuple.Item2))))
                                 .ToDictionaryIgnoringDuplicateKeys(tuple => tuple.Item1, tuple => tuple.Item2, StringComparer.OrdinalIgnoreCase);

            if (internalDiagnosticsMessageSink != null)
                internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver..ctor] Unmanaged assembly map includes: {string.Join(",", managedAssemblyMap.Keys.Select(k => $"'{k}'").OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}]"));

            Default.Resolving += OnResolving;
        }

        /// <inheritdoc/>
        public void Dispose()
            => Default.Resolving -= OnResolving;

        /// <inheritdoc/>
        protected override Assembly Load(AssemblyName assemblyName)
            => Default.LoadFromAssemblyName(assemblyName);

        /// <inheritdoc/>
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            if (internalDiagnosticsMessageSink != null)
                internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.LoadUnmanagedDll] Attempting resolution of unmanaged assembly '{unmanagedDllName}'"));

            if (unmanagedAssemblyMap.TryGetValue(unmanagedDllName, out var libraryTuple))
            {
                var library = libraryTuple.Item1;
                var assetGroup = libraryTuple.Item2;
                var wrapper = new CompilationLibrary(library.Type, library.Name, library.Version, library.Hash,
                                                     assetGroup.AssetPaths, library.Dependencies, library.Serviceable);

                var assemblies = new List<string>();
                if (assemblyResolver.TryResolveAssemblyPaths(wrapper, assemblies))
                {
                    var resolvedAssemblyPath = assemblies.FirstOrDefault(a => string.Equals(unmanagedDllName, Path.GetFileName(a), StringComparison.OrdinalIgnoreCase));
                    if (resolvedAssemblyPath != null)
                    {
                        var assembly = LoadUnmanagedDllFromPath(resolvedAssemblyPath);
                        if (assembly != null)
                            return assembly;
                    }
                }
            }

            return base.LoadUnmanagedDll(unmanagedDllName);
        }

        Assembly OnResolving(AssemblyLoadContext context, AssemblyName name)
        {
            if (internalDiagnosticsMessageSink != null)
                internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.OnResolving] Attempting resolution of managed assembly '{name.Name}'"));

            // Try to find dependency from .deps.json
            if (managedAssemblyMap.TryGetValue(name.Name, out var libraryTuple))
            {
                var library = libraryTuple.Item1;
                var assetGroup = libraryTuple.Item2;
                var wrapper = new CompilationLibrary(library.Type, library.Name, library.Version, library.Hash,
                                                     assetGroup.AssetPaths, library.Dependencies, library.Serviceable);

                var assemblies = new List<string>();
                if (assemblyResolver.TryResolveAssemblyPaths(wrapper, assemblies))
                {
                    var resolvedAssemblyPath = assemblies.FirstOrDefault(a => string.Equals(name.Name, Path.GetFileNameWithoutExtension(a), StringComparison.OrdinalIgnoreCase));
                    if (resolvedAssemblyPath != null)
                    {
                        var assembly = LoadFromAssemblyPath(resolvedAssemblyPath);
                        if (assembly != null)
                        {
                            if (internalDiagnosticsMessageSink != null)
                                internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.OnResolving] Successful resolution via dependencies: '{resolvedAssemblyPath}'"));

                            return assembly;
                        }
                        else
                        {
                            if (internalDiagnosticsMessageSink != null)
                                internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.OnResolving] Found assembly path '{resolvedAssemblyPath}' but the assembly would not load"));
                        }
                    }
                    else
                    {
                        if (internalDiagnosticsMessageSink != null)
                            internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.OnResolving] Found a resolved path, but could not map a filename in [{string.Join(",", assemblies.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => $"'{k}'"))}]"));
                    }
                }
                else
                {
                    if (internalDiagnosticsMessageSink != null)
                        internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.OnResolving] Found in dependency map, but unable to resolve a path in [{string.Join(",", assetGroup.AssetPaths.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => $"'{k}'"))}]"));
                }
            }

            // Try to find dependency in the local folder
            var assemblyPath = Path.Combine(assemblyFolder, name.Name);

            foreach (var extension in new[] { ".dll", ".exe" })
                try
                {
                    var resolvedAssemblyPath = assemblyPath + extension;
                    var assembly = LoadFromAssemblyPath(resolvedAssemblyPath);
                    if (assembly != null)
                    {
                        if (internalDiagnosticsMessageSink != null)
                            internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.OnResolving] Successful resolution via local folder: '{resolvedAssemblyPath}'"));

                        return assembly;
                    }
                }
                catch { }

            if (internalDiagnosticsMessageSink != null)
                internalDiagnosticsMessageSink.OnMessage(new DiagnosticMessage($"[NetCoreAssemblyDependencyResolver.OnResolving] Failed resolution, passed down to next resolver"));

            return null;
        }
    }
}

#endif
