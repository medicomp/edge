using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
#if NETSTANDARD1_5
using System.Linq;
using Microsoft.Extensions.DependencyModel;
#endif

namespace EdgeJs
{
    public class Edge
    {
        static object syncRoot = new object();
        static bool initialized;
        static Func<object, Task<object>> compileFunc;
        static ManualResetEvent waitHandle = new ManualResetEvent(false);

        static string assemblyDirectory;
        static string AssemblyDirectory
        {
            get
            {
                if (assemblyDirectory == null)
                {
                    assemblyDirectory = Environment.GetEnvironmentVariable("EDGE_BASE_DIR");

                    if (String.IsNullOrEmpty(assemblyDirectory))
                    {
#if NETSTANDARD1_5
                        string codeBase = typeof(Edge).GetTypeInfo().Assembly.CodeBase;
#else
                        string codeBase = typeof(Edge).Assembly.CodeBase;
#endif
                        UriBuilder uri = new UriBuilder(codeBase);
                        string path = Uri.UnescapeDataString(uri.Path);

                        assemblyDirectory = Path.GetDirectoryName(path);
                    }
                }

                return assemblyDirectory;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<object> InitializeInternal(object input)
        {
            compileFunc = (Func<object, Task<object>>)input;
            initialized = true;
            waitHandle.Set();

            return Task.FromResult((object)null);
        }

        // Find the entry point with `dumpbin /exports node.exe`, look for Start@node
        [DllImport("node", EntryPoint = "Start", CallingConvention = CallingConvention.Cdecl)]
        static extern int NodeStart(int argc, string[] argv);

#if !NETSTANDARD1_5
        [DllImport("kernel32.dll", EntryPoint = "LoadLibrary")]
        static extern int LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpLibFileName);
#endif

        public static Func<object,Task<object>> Func(string code)
        {
            if (!initialized)
            {
                lock (syncRoot)
                {
                    if (!initialized)
                    {
                        string[] nodeParams = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_NODE_PARAMS"))
                            ? new string[0]
                            : Environment.GetEnvironmentVariable("EDGE_NODE_PARAMS").Split(' ');

#if NETSTANDARD1_5
                        Environment.SetEnvironmentVariable("EDGE_USE_CORECLR", "1");
                        Environment.SetEnvironmentVariable("EDGE_APP_ROOT", AppContext.BaseDirectory);

                        string nativeLibraryPath = Path.Combine(AssemblyDirectory, "..", "..", DependencyContext.Default.RuntimeLibraries.Single(l => l.Name == "Edge.js").NativeLibraryGroups[0].AssetPaths[0].Replace('/', Path.DirectorySeparatorChar));
                        
                        Environment.SetEnvironmentVariable("EDGE_NATIVE_LIBRARIES_PATH", Path.GetDirectoryName(nativeLibraryPath));
#else
                        if (IntPtr.Size == 4)
                        {
                            LoadLibrary(AssemblyDirectory + @"\edge\x86\node.dll");
                        }
                        else if (IntPtr.Size == 8)
                        {
                            LoadLibrary(AssemblyDirectory + @"\edge\x64\node.dll");
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                "Unsupported architecture. Only x86 and x64 are supported.");
                        }
#endif

                        Thread v8Thread = new Thread(() => 
                        {
                            List<string> argv = new List<string>();
                            argv.Add("node");

                            foreach (string param in nodeParams)
                            {
                                argv.Add(param);
                            }

                            argv.Add(Path.Combine(AssemblyDirectory, "..", "..", "content", "edge", "double_edge.js"));
                            NodeStart(argv.Count, argv.ToArray());
                            waitHandle.Set();
                        });

                        v8Thread.IsBackground = true;
                        v8Thread.Start();
                        waitHandle.WaitOne();

                        if (!initialized)
                        {
                            throw new InvalidOperationException("Unable to initialize Node.js runtime.");
                        }
                    }
                }
            }

            if (compileFunc == null)
            {
                throw new InvalidOperationException("Edge.Func cannot be used after Edge.Close had been called.");
            }

            Task<object> task = compileFunc(code);
            task.Wait();

            return (Func<object, Task<object>>)task.Result;
        }
    }
}
