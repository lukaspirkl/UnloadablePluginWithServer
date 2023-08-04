using Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal class Program
{
    private static void Main(string[] args)
    {
        var pluginService = new PluginService();
        pluginService.LoadPlugin();

        Console.WriteLine("Press key to unload plugin");
        Console.ReadKey();
        
        pluginService.UnloadPluginAsync().Wait();

        Console.WriteLine("Press key to stop");
        Console.ReadKey();
    }
}

public class PluginService
{
    IServerPlugin? m_Plugin;
    PluginLoadContext? m_LoadContext;
    WebApplication? m_Server;

    public void LoadPlugin()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plugin", "plugin.dll");
        Console.WriteLine($"Load plugin from '{path}'.");
        m_LoadContext = new PluginLoadContext(path, AssemblyName.GetAssemblyName(path));
        m_Plugin = m_LoadContext.LoadPlugin();

        var builder = WebApplication.CreateBuilder();
        // TODO: Plugin register its own services/middlewares/endpoints
        m_Server = builder.Build();
        m_Server.Start(); // When this line is commented, the load context can be unloaded without problem
    }

    
    public async Task UnloadPluginAsync()
    {
        var alcWeakRef = await UnloadAsync();
        
        int count = 0;
        for (int i = 0; alcWeakRef.IsAlive && (i < 10); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            count++;
        }

        if (alcWeakRef.IsAlive)
        {
            Console.WriteLine("Failed to unload AssemblyLoadContext for plugin!");
        }
        else
        {
            Console.WriteLine($"Plugin unloaded after {count} attempts.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> UnloadAsync()
    {
        if (m_Server != null)
        {
            await m_Server.StopAsync();
            await m_Server.DisposeAsync();
            m_Server = null;
        }

        m_Plugin = null;

        var weakRef = new WeakReference(m_LoadContext);
        m_LoadContext?.Unload();
        m_LoadContext = null;
        return weakRef;
    }
}


public class PluginLoadContext : AssemblyLoadContext
{
    private AssemblyDependencyResolver m_DependencyResolver;

    public string PluginAssemblyPath { get; }
    public AssemblyName PluginAssemblyName { get; }

    /// <summary>
    /// For debugging.
    /// </summary>
    public List<string> LocalAssemblies { get; } = new List<string>();

    /// <summary>
    /// For debugging.
    /// </summary>
    public List<string> GlobalAssemblies { get; } = new List<string>();

    public PluginLoadContext(string assemblyPath, AssemblyName assemblyName)
        : base(assemblyName.Name, isCollectible: true)
    {
        PluginAssemblyPath = assemblyPath;
        PluginAssemblyName = assemblyName;

        m_DependencyResolver = new AssemblyDependencyResolver(assemblyPath);
    }

    public IServerPlugin? LoadPlugin()
    {
        LocalAssemblies.Add(PluginAssemblyName.FullName);
        var assembly = LoadAsStream(PluginAssemblyPath);
        foreach (var type in assembly.GetTypes())
        {
            if (typeof(IServerPlugin).IsAssignableFrom(type))
            {
                return Activator.CreateInstance(type) as IServerPlugin;
            }
        }

        return null;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = m_DependencyResolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            LocalAssemblies.Add(assemblyName.FullName);
            return LoadAsStream(assemblyPath);
        }

        GlobalAssemblies.Add(assemblyName.FullName);
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = m_DependencyResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }

    private Assembly LoadAsStream(string assemblyPath)
    {
        using var file = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            using var pdbFile = File.Open(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LoadFromStream(file, pdbFile);
        }

        return LoadFromStream(file);
    }
}
