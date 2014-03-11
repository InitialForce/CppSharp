using System.Collections.Generic;
using System.IO;
using CppSharp.AST;

namespace CppSharp
{
    internal class FFMS2Library : ILibrary
    {
        public void Preprocess(Driver driver, ASTContext ctx, IEnumerable<ASTContext> dependentContexts = null)
        {
        }

        public void Postprocess(Driver driver, ASTContext lib, IEnumerable<ASTContext> dependentContexts = null)
        {
        }

        public void Setup(Driver driver)
        {
            driver.Options.LibraryName = "FFMS2";
            driver.Options.Headers.Add(@"C:\WORK\REPOS-SC\ffms2\include\ffms.h");
            driver.Options.OutputDir = Path.Combine(@"C:\WORK\REPOS-SC\FFMS2_bindings");
            driver.Options.OutputNamespace = "FFMS2";
            driver.Options.CustomDllImport = "FFMS2_DLL_NAME";
        }

        public void SetupPasses(Driver driver)
        {
        }
    }
}