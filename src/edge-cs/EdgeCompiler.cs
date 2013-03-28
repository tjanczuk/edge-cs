using Microsoft.CSharp;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public class EdgeCompiler
{
    static readonly Regex referencesRegex = new Regex(@"\/\/\#r\s+""[^""]+""\s*", RegexOptions.Multiline);
    static readonly Regex referenceRegex = new Regex(@"\/\/\#r\s+""([^""]+)""\s*");

    public Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters)
    {
        string source = (string)parameters["source"];

        // read source from file
        if (source.EndsWith(".cs", StringComparison.InvariantCultureIgnoreCase)
            || source.EndsWith(".csx", StringComparison.InvariantCultureIgnoreCase))
        {
            source = File.ReadAllText(source);
        }

        // add assembly references provided explicitly through parameters
        List<string> references = new List<string>();
        object v;
        if (parameters.TryGetValue("references", out v))
        {
            foreach (object reference in (object[])v)
            {
                references.Add((string)reference);
            }
        }

        // add assembly references provided in code as //#r "assemblyname" comments
        foreach (Match match in referencesRegex.Matches(source))
        {
            Match referenceMatch = referenceRegex.Match(match.Value);
            if (referenceMatch.Success)
            {
                references.Add(referenceMatch.Groups[1].Value);
            }
        }

        // try to compile source code as a class library
        Assembly assembly;
        string errorsClass;
        if (!this.TryCompile(source, references, out errorsClass, out assembly))
        {
            // try to compile source code as an async lambda expression
            string errorsLambda;
            source = 
                "using System;\n"
                + "using System.Threading.Tasks;\n"
                + "public class Startup {\n"
                + "    public async Task<object> Invoke(object ___input) {\n"
                + "        Func<object, Task<object>> func = " + source + ";\n"
                + "        return await func(___input);\n"
                + "    }\n"
                + "}";

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
        Type startupType = assembly.GetType((string)parameters["typeName"], true, true);
        object instance = Activator.CreateInstance(startupType, false);
        MethodInfo invokeMethod = startupType.GetMethod((string)parameters["methodName"], BindingFlags.Instance | BindingFlags.Public);
        if (invokeMethod == null)
        {
            throw new InvalidOperationException("Unable to access CLR method to wrap through reflection. Make sure it is a public instance method.");
        }

        // create a Func<object,Task<object>> delegate around the method invocation using reflection
        Func<object,Task<object>> result = (input) => 
        {
            return (Task<object>)invokeMethod.Invoke(instance, new object[] { input });
        };

        return result;
    }

    bool TryCompile(string source, List<string> references, out string errors, out Assembly assembly)
    {
        bool result = false;
        assembly = null;
        errors = null;

        Dictionary<string, string> options = new Dictionary<string, string> { { "CompilerVersion", "v4.0" } };
        CSharpCodeProvider csc = new CSharpCodeProvider(options);
        CompilerParameters parameters = new CompilerParameters();
        parameters.GenerateInMemory = true;
        parameters.ReferencedAssemblies.AddRange(references.ToArray());
        parameters.ReferencedAssemblies.Add("System.dll");
        CompilerResults results = csc.CompileAssemblyFromSource(parameters, source);
        if (results.Errors.HasErrors)
        {
            foreach (CompilerError error in results.Errors)
            {
                if (errors == null)
                {
                    errors = error.ToString();
                }
                else
                {
                    errors += "\n" + error.ToString();
                }
            }
        }
        else
        {
            assembly = results.CompiledAssembly;
            result = true;
        }

        return result;
    }
}
