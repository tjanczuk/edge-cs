using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Framework.Runtime;
using NuGet;

// ReSharper disable once CheckNamespace
public class EdgeCompiler
{
    private static readonly Regex ReferenceRegex = new Regex(@"^[\ \t]*(?:\/{2})?\#r[\ \t]+""([^""]+)""", RegexOptions.Multiline);
    private static readonly Regex UsingRegex = new Regex(@"^[\ \t]*(using[\ \t]+[^\ \t]+[\ \t]*\;)", RegexOptions.Multiline);
    private static readonly bool DebuggingEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_DEBUG"));
    private static readonly bool DebuggingSelfEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_DEBUG_SELF"));
    private static readonly bool CacheEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_CACHE"));
    private static readonly Dictionary<string, Func<object, Task<object>>> FuncCache = new Dictionary<string, Func<object, Task<object>>>();
    private static readonly FrameworkName TargetFrameworkName = new FrameworkName("DNXCore,Version=v5.0");
    private Func<Stream, Assembly> _assemblyLoader;

    public void SetAssemblyLoader(Func<Stream, Assembly> assemblyLoader)
    {
        _assemblyLoader = assemblyLoader;
    }

    public void DebugMessage(string format, params object[] args)
    {
        if (DebuggingSelfEnabled)
        {
            Console.WriteLine(format, args);
        }
    }

    public Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters)
    {
        string source = (string) parameters["source"];
        string lineDirective = string.Empty;
        string fileName = null;
        int lineNumber = 1;

        // Read source from file
        if (source.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
        {
            // Retain filename for debugging purposes
            if (DebuggingEnabled)
            {
                fileName = source;
            }

            source = File.ReadAllText(source);
        }

        DebugMessage("EdgeCompiler::CompileFunc (CLR) - Func cache size: {0}", FuncCache.Count);

        string originalSource = source;

        if (FuncCache.ContainsKey(originalSource))
        {
            DebugMessage("EdgeCompiler::CompileFunc (CLR) - Serving func from cache");
            return FuncCache[originalSource];
        }

        DebugMessage("EdgeCompiler::CompileFunc (CLR) - Func not found in cache, compiling");

        // Add assembly references provided explicitly through parameters, along with some default ones
        Dictionary<string, string> references = new Dictionary<string, string>
        {
            {
                "System.Runtime", ""
            },
            {
                "System.Threading.Tasks", ""
            },
            {
                "System.Dynamic.Runtime", ""
            },
            {
                "Microsoft.CSharp", ""
            }
        };

        object providedReferences;

        if (parameters.TryGetValue("references", out providedReferences))
        {
            IDictionary<string, object> referencesDictionary = providedReferences as IDictionary<string, object>;

            if (referencesDictionary != null)
            {
                foreach (string reference in referencesDictionary.Keys)
                {
                    references[reference] = (string) referencesDictionary[reference];
                }
            }

            else
            {
                foreach (object reference in (object[]) providedReferences)
                {
                    references[(string) reference] = "";
                }
            }
        }

        // Add assembly references provided in code as [//]#r "assembly name" lines
        Match match = ReferenceRegex.Match(source);

        while (match.Success)
        {
            references[match.Groups[1].Value] = "";
            source = source.Substring(0, match.Index) + source.Substring(match.Index + match.Length);
            match = ReferenceRegex.Match(source);
        }

        if (DebuggingEnabled)
        {
            object jsFileName;

            if (parameters.TryGetValue("jsFileName", out jsFileName))
            {
                fileName = (string) jsFileName;
                lineNumber = (int) parameters["jsLineNumber"];
            }

            if (!string.IsNullOrEmpty(fileName))
            {
                lineDirective = string.Format("#line {0} \"{1}\"", lineNumber, fileName);
            }
        }

        // Try to compile source code as a class library
        Assembly assembly;
        string errorsClass;

        if (!TryCompile(lineDirective + source, references, out errorsClass, out assembly))
        {
            // Try to compile source code as an async lambda expression

            // Extract using statements first
            string usings = "";
            match = UsingRegex.Match(source);

            while (match.Success)
            {
                usings += match.Groups[1].Value;
                source = source.Substring(0, match.Index) + source.Substring(match.Index + match.Length);
                match = UsingRegex.Match(source);
            }

            string errorsLambda;
            source = usings + @"
using System;
using System.Threading.Tasks;

public class Startup 
{
    public async Task<object> Invoke(object ___input) 
    {
" + lineDirective + @"
        Func<object, Task<object>> func = " + source + @";
#line hidden
        return await func(___input);
    }
}";

            DebugMessage("EdgeCompiler::CompileFunc (CLR) - Trying to compile async lambda expression:{0}{1}", Environment.NewLine, source);

            if (!TryCompile(source, references, out errorsLambda, out assembly))
            {
                throw new InvalidOperationException("Unable to compile C# code.\n----> Errors when compiling as a CLR library:\n" + errorsClass +
                                                    "\n----> Errors when compiling as a CLR async lambda expression:\n" + errorsLambda);
            }
        }

        // Extract the entry point to a class method
        Type startupType = assembly.GetType((string) parameters["typeName"]);

        if (startupType == null)
        {
            throw new TypeLoadException("Could not load type '" + (string) parameters["typeName"] + "'");
        }

        object instance = Activator.CreateInstance(startupType);
        MethodInfo invokeMethod = startupType.GetMethod((string) parameters["methodName"], BindingFlags.Instance | BindingFlags.Public);

        if (invokeMethod == null)
        {
            throw new InvalidOperationException("Unable to access CLR method to wrap through reflection. Make sure it is a public instance method.");
        }

        // Ereate a Func<object,Task<object>> delegate around the method invocation using reflection
        Func<object, Task<object>> result = input => (Task<object>) invokeMethod.Invoke(instance, new object[]
        {
            input
        });

        if (CacheEnabled)
        {
            FuncCache[originalSource] = result;
        }

        return result;
    }

    private bool TryCompile(string source, IDictionary<string, string> references, out string errors, out Assembly assembly)
    {
        assembly = null;
        errors = null;

        string projectDirectory = Environment.GetEnvironmentVariable("EDGE_APP_ROOT") ?? Directory.GetCurrentDirectory();
        PackageRepository packageRepository = new PackageRepository(NuGetDependencyResolver.ResolveRepositoryPath(projectDirectory));

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        List<MetadataReference> metadataReferences = new List<MetadataReference>();

        DebugMessage("EdgeCompiler::TryCompile (CLR) - Resolving {0} references", references.Count);

        // Search the NuGet package repository for each reference
        foreach (string reference in references.Keys)
        {
            DebugMessage("EdgeCompiler::TryCompile (CLR) - Searching for {0}", reference);

            // If the reference looks like a filename, try to load it directly; if we fail and the reference name does not contain a path separator (like
            // System.Data.dll), we fall back to stripping off the extension and treating the reference like a NuGet package
            if (reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (reference.Contains(Path.DirectorySeparatorChar))
                {
                    metadataReferences.Add(MetadataReference.CreateFromFile(Path.IsPathRooted(reference)
                        ? reference
                        : Path.Combine(projectDirectory, reference)));
                    continue;
                }

                if (File.Exists(Path.Combine(projectDirectory, reference)))
                {
                    metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(projectDirectory, reference)));
                    continue;
                }
            }

            string referenceName = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? reference.Substring(0, reference.Length - 4)
                : reference;
            string referencePath = null;
            PackageInfo referencePackage;

            // If the package version is omitted, use the most recent version in the repository
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

            // Search for an assembly to reference during compilation in the following order:
            //  1. An assembly in the ref/ directory whose filename matches the package name and where the target framework matches
            //  2. An assembly in the ref/ directory whose filename matches the package name and where the target framework is not specified
            //  3. An assembly elsewhere in the package directory structure whose filename matches the package name and where the target framework matches
            IPackageFile[] packageFiles = referencePackage.Package.GetFiles().ToArray();
            IPackageFile referenceAssembly =
                (packageFiles.FirstOrDefault(
                    f =>
                        f.Path.StartsWith("ref" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && f.TargetFramework == TargetFrameworkName &&
                        f.Path.EndsWith(Path.DirectorySeparatorChar + referenceName + ".dll", StringComparison.OrdinalIgnoreCase)) ??
                 packageFiles.FirstOrDefault(
                     f =>
                         f.Path.StartsWith("ref" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && f.TargetFramework == null &&
                         f.Path.EndsWith(Path.DirectorySeparatorChar + referenceName + ".dll", StringComparison.OrdinalIgnoreCase))) ??
                packageFiles.SingleOrDefault(
                    f =>
                        f.TargetFramework == TargetFrameworkName &&
                        f.Path.EndsWith(Path.DirectorySeparatorChar + referenceName + ".dll", StringComparison.OrdinalIgnoreCase));

            if (referenceAssembly != null)
            {
                referencePath = Path.Combine(packageRepository.RepositoryRoot.Root, referenceName, referencePackage.Version.ToString(), referenceAssembly.Path);
                DebugMessage("EdgeCompiler::TryCompile (CLR) - Found the assembly for {0} at {1}", referenceName, referencePath);
            }

            if (String.IsNullOrEmpty(referencePath))
            {
                throw new Exception(String.Format("Unable to load the reference to {0}.", referenceName));
            }

            metadataReferences.Add(MetadataReference.CreateFromFile(referencePath));
        }

        CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: DebuggingEnabled
            ? OptimizationLevel.Debug
            : OptimizationLevel.Release);

        DebugMessage("EdgeCompiler::TryCompile (CLR) - Starting compilation");
        
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
                    compilationResults.Diagnostics.Where(
                        diagnostic =>
                            diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Severity == DiagnosticSeverity.Warning);

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

                DebugMessage("EdgeCompiler::TryCompile (CLR) - Compilation failed with the following errors: {0}{1}", Environment.NewLine, errors);
                return false;
            }

            memoryStream.Seek(0, SeekOrigin.Begin);
            assembly = _assemblyLoader(memoryStream);

            DebugMessage("EdgeCompiler::TryCompile (CLR) - Compilation completed successfully");
            return true;
        }
    }
}