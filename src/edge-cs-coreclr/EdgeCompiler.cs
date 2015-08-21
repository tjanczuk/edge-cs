using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Framework.Runtime;
using NuGet;

public class EdgeCompiler
{
    static readonly Regex referenceRegex = new Regex(@"^[\ \t]*(?:\/{2})?\#r[\ \t]+""([^""]+)""", RegexOptions.Multiline);
    static readonly Regex usingRegex = new Regex(@"^[\ \t]*(using[\ \t]+[^\ \t]+[\ \t]*\;)", RegexOptions.Multiline);
    static readonly bool debuggingEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_DEBUG"));
    static readonly bool debuggingSelfEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_DEBUG_SELF"));
    static readonly bool cacheEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_CACHE"));
    static Dictionary<string, Func<object, Task<object>>> funcCache = new Dictionary<string, Func<object, Task<object>>>();
    static StreamAssemblyLoadContext AssemblyLoadContext = new StreamAssemblyLoadContext();
    static readonly FrameworkName targetFrameworkName = new FrameworkName("DNXCore,Version=v5.0");

    private class StreamAssemblyLoadContext : AssemblyLoadContext
    {
        [SecuritySafeCritical]
        protected override Assembly Load(AssemblyName assemblyName)
        {
            return Assembly.Load(assemblyName);
        }

        public Assembly LoadFrom(Stream assembly)
        {
            return LoadFromStream(assembly);
        }
    }

    public Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters)
    {
        string source = (string)parameters["source"];
        string lineDirective = string.Empty;
        string fileName = null;
        int lineNumber = 1;

        // read source from file
        if (source.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
        {
            // retain fileName for debugging purposes
            if (debuggingEnabled)
            {
                fileName = source;
            }

            source = File.ReadAllText(source);
        }

        if (debuggingSelfEnabled)
        {
            Console.WriteLine("Func cache size: " + funcCache.Count);
        }

        var originalSource = source;
        if (funcCache.ContainsKey(originalSource))
        {
            if (debuggingSelfEnabled)
            {
                Console.WriteLine("Serving func from cache.");
            }

            return funcCache[originalSource];
        }
        else if (debuggingSelfEnabled)
        {
            Console.WriteLine("Func not found in cache. Compiling.");
        }

        // add assembly references provided explicitly through parameters
        Dictionary<string, string> references = new Dictionary<string, string>
        {
            {"System.Runtime", ""},
            {"System.Threading.Tasks", ""},
            {"System.Dynamic.Runtime", ""}
        };

        object v;
        if (parameters.TryGetValue("references", out v))
        {
            if (v is IDictionary<string, object>)
            {
                foreach (string reference in ((IDictionary<string, object>)v).Keys)
                {
                    references[reference] = (string)((IDictionary<string, object>)v)[reference];
                }
            }

            else
            {
                foreach (object reference in (object[]) v)
                {
                    references[(string) reference] = "";
                }
            }
        }

        // add assembly references provided in code as [//]#r "assemblyname" lines
        Match match = referenceRegex.Match(source);
        while (match.Success)
        {
            references[match.Groups[1].Value] = "";
            source = source.Substring(0, match.Index) + source.Substring(match.Index + match.Length);
            match = referenceRegex.Match(source);
        }

        if (debuggingEnabled)
        {
            object jsFileName;
            if (parameters.TryGetValue("jsFileName", out jsFileName))
            {
                fileName = (string)jsFileName;
                lineNumber = (int)parameters["jsLineNumber"];
            }

            if (!string.IsNullOrEmpty(fileName))
            {
                lineDirective = string.Format("#line {0} \"{1}\"\n", lineNumber, fileName);
            }
        }

        // try to compile source code as a class library
        Assembly assembly;
        string errorsClass;
        if (!this.TryCompile(lineDirective + source, references, out errorsClass, out assembly))
        {
            // try to compile source code as an async lambda expression

            // extract using statements first
            string usings = "";
            match = usingRegex.Match(source);
            while (match.Success)
            {
                usings += match.Groups[1].Value;
                source = source.Substring(0, match.Index) + source.Substring(match.Index + match.Length);
                match = usingRegex.Match(source);
            }

            string errorsLambda;
            source =
                usings + "using System;\n"
                + "using System.Threading.Tasks;\n"
                + "public class Startup {\n"
                + "    public async Task<object> Invoke(object ___input) {\n"
                + lineDirective
                + "        Func<object, Task<object>> func = " + source + ";\n"
                + "#line hidden\n"
                + "        return await func(___input);\n"
                + "    }\n"
                + "}";

            if (debuggingSelfEnabled)
            {
                Console.WriteLine("Edge-cs trying to compile async lambda expression:");
                Console.WriteLine(source);
            }

            if (!TryCompile(source, references, out errorsLambda, out assembly))
            {
                throw new InvalidOperationException(
                    "Unable to compile C# code.\n----> Errors when compiling as a CLR library:\n"
                    + errorsClass
                    + "\n----> Errors when compiling as a CLR async lambda expression:\n"
                    + errorsLambda);
            }
        }

        // extract the entry point to a class method
        Type startupType = assembly.GetType((string)parameters["typeName"]);

        if (startupType == null)
        {
            throw new TypeLoadException("Type not found: " + (string)parameters["typeName"]);
        }

        object instance = Activator.CreateInstance(startupType);
        MethodInfo invokeMethod = startupType.GetMethod((string)parameters["methodName"], BindingFlags.Instance | BindingFlags.Public);

        if (invokeMethod == null)
        {
            throw new InvalidOperationException("Unable to access CLR method to wrap through reflection. Make sure it is a public instance method.");
        }

        // create a Func<object,Task<object>> delegate around the method invocation using reflection
        Func<object, Task<object>> result = (input) =>
        {
            return (Task<object>)invokeMethod.Invoke(instance, new object[] { input });
        };

        if (cacheEnabled)
        {
            funcCache[originalSource] = result;
        }

        return result;
    }

    bool TryCompile(string source, IDictionary<string, string> references, out string errors, out Assembly assembly)
    {
        assembly = null;
        errors = null;

        string projectDirectory = Environment.GetEnvironmentVariable("EDGE_APP_ROOT") ?? Directory.GetCurrentDirectory();
        PackageRepository packageRepository = new PackageRepository(NuGetDependencyResolver.ResolveRepositoryPath(projectDirectory));

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        List<MetadataReference> metadataReferences = new List<MetadataReference>();

        if (debuggingSelfEnabled)
        {
            Console.WriteLine("Resolving {0} references", references.Count);
        }

        foreach (string reference in references.Keys)
        {
            if (debuggingSelfEnabled)
            {
                Console.WriteLine("Searching for {0}", reference);
            }

            if (reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (reference.Contains(Path.DirectorySeparatorChar))
                {
                    metadataReferences.Add(MetadataReference.CreateFromFile(Path.IsPathRooted(reference)
                        ? reference
                        : Path.Combine(projectDirectory, reference)));
                    continue;
                }

                else
                {
                    if (File.Exists(Path.Combine(projectDirectory, reference)))
                    {
                        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(projectDirectory, reference)));
                        continue;
                    }
                }
            }

            string referenceName = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? reference.Substring(0, reference.Length - 4)
                : reference;
            string referencePath = null;
            PackageInfo referencePackage = null;
            IPackageFile referenceAssembly = null;

            if (String.IsNullOrEmpty(references[reference]))
            {
                referencePackage = packageRepository.FindPackagesById(referenceName).OrderByDescending(p => p.Version).FirstOrDefault();
            }

            else
            {
                SemanticVersion packageVersion = SemanticVersion.Parse(references[reference]);
                referencePackage = packageRepository.FindPackagesById(referenceName).OrderBy(p => p.Version).FirstOrDefault(p => p.Version == packageVersion);
            }

            if (referencePackage == null)
            {
                throw new Exception(String.Format("Unable to find the NuGet package for {0}.", referenceName));
            }

            IPackageFile[] packageFiles = referencePackage.Package.GetFiles().ToArray();

            referenceAssembly =
                packageFiles.FirstOrDefault(
                    f =>
                        f.Path.StartsWith("ref" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && f.TargetFramework == targetFrameworkName &&
                        f.Path.EndsWith(Path.DirectorySeparatorChar + referenceName + ".dll", StringComparison.OrdinalIgnoreCase));

            if (referenceAssembly == null)
            {
                referenceAssembly =
                    packageFiles.FirstOrDefault(
                        f =>
                            f.Path.StartsWith("ref" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && f.TargetFramework == null &&
                            f.Path.EndsWith(Path.DirectorySeparatorChar + referenceName + ".dll", StringComparison.OrdinalIgnoreCase));
            }

            if (referenceAssembly == null)
            {
                referenceAssembly =
                    packageFiles.SingleOrDefault(
                        f =>
                            f.TargetFramework == targetFrameworkName &&
                            f.Path.EndsWith(Path.DirectorySeparatorChar + referenceName + ".dll", StringComparison.OrdinalIgnoreCase));
            }

            if (referenceAssembly != null)
            {
                referencePath = Path.Combine(packageRepository.RepositoryRoot.Root, referenceName, referencePackage.Version.ToString(), referenceAssembly.Path);

                if (debuggingSelfEnabled)
                {
                    Console.WriteLine("Found the assembly for {0} at {1}", referenceName, referencePath);
                }
            }

            if (String.IsNullOrEmpty(referencePath))
            {
                throw new Exception(String.Format("Unable to load the reference to {0}.", referenceName));
            }

            metadataReferences.Add(MetadataReference.CreateFromFile(referencePath));
        }

        CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: debuggingEnabled
            ? OptimizationLevel.Debug
            : OptimizationLevel.Release);

        if (debuggingSelfEnabled)
        {
            Console.WriteLine("Starting compilation");
        }

        CSharpCompilation compilation = CSharpCompilation.Create(Guid.NewGuid() + ".dll", new SyntaxTree[]
        {
            syntaxTree
        }, metadataReferences, compilationOptions);

        using (MemoryStream memoryStream = new MemoryStream())
        {
            EmitResult compilationResults = compilation.Emit(memoryStream);

            if (!compilationResults.Success)
            {
                IEnumerable<Diagnostic> failures =
                    compilationResults.Diagnostics.Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Severity == DiagnosticSeverity.Warning);

                foreach (Diagnostic diagnostic in failures)
                {
                    if (errors == null)
                    {
                        errors = String.Format("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                    }

                    else
                    {
                        errors += String.Format("\n{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                    }
                }

                if (debuggingSelfEnabled)
                {
                    Console.WriteLine("Compilation failed with the following errors: {0}{1}", Environment.NewLine, errors);
                }

                return false;
            }

            else
            {
                memoryStream.Seek(0, SeekOrigin.Begin);
                assembly = AssemblyLoadContext.LoadFrom(memoryStream);

                if (debuggingSelfEnabled)
                {
                    Console.WriteLine("Compilation completed successfully");
                }

                return true;
            }
        }
    }
}
