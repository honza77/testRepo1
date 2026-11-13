using System;
using System.Reflection;

namespace HelloWorld
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? asm.GetName().Version.ToString();
            Console.WriteLine("Hello, World! (C#, version {0})", version);
            Console.WriteLine("Running on {0} from {1}", Environment.MachineName, AppDomain.CurrentDomain.BaseDirectory);
            if (args.Length > 0)
                Console.WriteLine("Arguments: {0}", string.Join(" ", args));
            return 0;
        }
    }
}
