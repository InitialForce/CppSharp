using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using CppSharp;
using CppSharp.AST;
using CppSharp.Passes;
using Type = CppSharp.AST.Type;

namespace FFmpegBindings
{
    public interface IComplexLibrary : ILibrary
    {
        IEnumerable<IComplexLibrary> DependentLibraries { get; }
        string OutputNamespace { get; }
    }

    [DebuggerDisplay("{LibraryName}")]
    internal class FFmpegSubLibrary : IComplexLibrary
    {
        private readonly IEnumerable<string> _filesToIgnore;
        private readonly DirectoryInfo _includeDir;
        private readonly DirectoryInfo _outputDir;

        public FFmpegSubLibrary(DirectoryInfo includeDir, string libraryName, string dllName, DirectoryInfo outputDir,
            IEnumerable<string> filesToIgnore = null, IEnumerable<IComplexLibrary> dependentLibraries = null)
        {
            _includeDir = includeDir;
            if (!_includeDir.Exists)
                throw new DirectoryNotFoundException(_includeDir.FullName);

            LibraryName = libraryName;
            LibraryNameSpace = "lib" + LibraryName;
            DllName = dllName;

            _outputDir = outputDir;
            DependentLibraries = dependentLibraries ?? Enumerable.Empty<IComplexLibrary>();
            _filesToIgnore = filesToIgnore ?? Enumerable.Empty<string>();
            OutputNamespace = "ffmpeg";
        }

        public string LibraryName { get; private set; }
        public string DllName { get; private set; }
        public string LibraryNameSpace { get; private set; }
        public IEnumerable<IComplexLibrary> DependentLibraries { get; private set; }
        public string OutputNamespace { get; private set; }
 
        public virtual void Preprocess(Driver driver, ASTContext ctx, IEnumerable<ASTContext> dependentContexts)
        {
            // ignore the other ffmpeg sublibraries (e.g. don't include avutil stuff when generating for avcodec)
//            foreach (var dependentLibDriver in _dependentLibraries)
//            {
//                foreach (var translationUnit in dependentLibDriver.ASTContext.TranslationUnits)
//                {
//                    lib.TranslationUnits.Add(translationUnit);
//                }
//            }
//            foreach (DirectoryInfo subLibDir in _includeDir.GetDirectories())
//            {
//                if (subLibDir.Name.Contains(LibraryName))
//                    continue;
//
//                foreach (FileInfo headerFile in subLibDir.GetFiles())
//                {
//                    foreach (
//                        TranslationUnit unit in lib.TranslationUnits.FindAll(m => m.FilePath == headerFile.FullName))
//                    {
//                        unit.IsGenerated = false;
//                        unit.ExplicityIgnored = true;
//                    }
//                }
//            }

//          ILP32 	LP64 	LLP64 	ILP64
//char 	    8 	    8 	    8 	    8
//short 	16 	    16 	    16 	    16
//int 	    32 	    32 	    32 	    64
//long 	    32 	    64 	    32 	    64
//long long 64 	    64 	    64 	    64
//size_t 	32 	    64 	    64 	    64
//pointer 	32 	    64 	    64 	    64

//ptrdiff_t 	32 	64 	
//size_t 	    32 	64 	
//intptr_tuintptr_t, SIZE_T, SSIZE_T, INT_PTR, DWORD_PTR, etc 32 	64 
//time_t 	    32 	64


            ctx.ConvertTypesToPortable(t => t.Declaration.Name == "size_t", PrimitiveType.UIntPtr);
            ctx.ConvertTypesToPortable(t => t.Declaration.Name == "time_t", PrimitiveType.UIntPtr);
            ctx.ConvertTypesToPortable(t => t.Declaration.Name == "ptrdiff_t", PrimitiveType.UIntPtr);

            ctx.ChooseAndPromoteIncompleteClass();
//            lib.ResolveUnifyIncompleteClassDeclarationsFromSubLibs(DependentLibraries);
//            lib.ResolveUnifyIncompleteClassDeclarations();
            // it's not possible to handle va_list using p/invoke
            ctx.IgnoreFunctionsWithParameterTypeName("va_list");
        }

        public virtual void Postprocess(Driver driver, ASTContext lib, IEnumerable<ASTContext> dependentContexts)
        {
            var ourTranslationUnits = lib.TranslationUnits.Where(
                o => !dependentContexts.Any(d => d.TranslationUnits.Any(t => t == o.TranslationUnit)));

            lib.GenerateClassWithConstValuesFromMacros(ourTranslationUnits, LibraryNameSpace);
            foreach (TranslationUnit tu in ourTranslationUnits)
            {
                var wrappingClass = tu.FindClass(LibraryNameSpace);
                if (wrappingClass == null)
                {
                    wrappingClass = new Class {Name = LibraryNameSpace, Namespace = tu};
                }
                wrappingClass.Classes.AddRange(tu.Classes.Except(new List<Class> {wrappingClass}));
                foreach (Class decl in wrappingClass.Classes)
                {
                    decl.Namespace = wrappingClass;
                }
                tu.Classes.Clear();
                tu.Classes.Add(wrappingClass);

                wrappingClass.Functions.AddRange(tu.Functions);
                foreach (Function decl in wrappingClass.Functions)
                {
                    decl.Namespace = wrappingClass;
                }
                tu.Functions.Clear();

                wrappingClass.Enums.AddRange(tu.Enums);
                foreach (Enumeration decl in wrappingClass.Enums)
                {
                    decl.Namespace = wrappingClass;
                }
                tu.Enums.Clear();
            }
        }

        public virtual void Setup(Driver driver)
        {
            driver.Options.LibraryName = DllName;
            driver.Options.IncludeDirs.Add(_includeDir.FullName);
            driver.Options.OutputDir = Path.Combine(_outputDir.FullName, LibraryNameSpace);
            driver.Options.OutputNamespace = "ffmpeg";
//            driver.Options.OutputClass = LibraryNameSpace;
            string combine = Path.Combine(_includeDir.FullName, LibraryNameSpace);
            foreach (FileInfo headerFile in Directory.GetFiles(combine).Select(a => new FileInfo(a)))
            {
                string item = Path.Combine(LibraryNameSpace, headerFile.Name);
                if (ShouldIncludeHeader(headerFile))
                {
                    driver.Options.Headers.Add(item);
                }
            }
            foreach (var dependentLibrary in DependentLibraries)
            {
                driver.Options.DependentNameSpaces.Add(dependentLibrary.OutputNamespace);
            }
        }

        public virtual void SetupPasses(Driver driver)
        {
            driver.TranslationUnitPasses.AddPass(new RewriteDoublePointerFunctionParametersToRef());
        }

        protected virtual bool ShouldIncludeHeader(FileInfo headerFileName)
        {
            if (_filesToIgnore.Contains(headerFileName.Name))
                return false;
            return true;
        }
    }

    public class RewriteDoublePointerFunctionParametersToRef : TranslationUnitPass
    {
        public override bool VisitFunctionDecl(Function function)
        {
            foreach (var parameter in function.Parameters.Where(IsDoublePointer))
            {
                parameter.Usage = ParameterUsage.InOut;
                parameter.QualifiedType = new QualifiedType
                {
                    Type = ((PointerType) parameter.Type).Pointee,
                    Qualifiers = parameter.QualifiedType.Qualifiers
                };
            }
            return true;
        }

        private bool IsDoublePointer(Parameter arg)
        {
            var type = arg.Type as PointerType;
            if(type != null)
            {
                return type.Pointee.Desugar() is PointerType;
            }
            return false;
        }
    }
    
    public static class X
    {
        public static Field GenerateConstValueFromMacro(this ASTContext context,
            MacroDefinition macro)
        {
            var builtinTypeExpression = PrimitiveTypeExpression.TryCreate(macro.Expression);
            if (builtinTypeExpression == null)
                return null;
            var valueType = new QualifiedType(new BuiltinType(builtinTypeExpression.Type))
            {
                Qualifiers = new TypeQualifiers {IsConst = true}
            };
            var item = new Field
            {
                Name = macro.Name,
                DebugText = macro.DebugText,
                Access = AccessSpecifier.Public,
                Expression =
                    builtinTypeExpression,
                QualifiedType = valueType
            };

            return item;
        }

        public static void GenerateClassWithConstValuesFromMacros(this ASTContext context, IEnumerable<TranslationUnit> ourTranslationUnits, string className)
        {
            foreach (TranslationUnit tu in ourTranslationUnits)
            {
                var wrappingClass = tu.FindClass(className);
                if (wrappingClass == null)
                {
                    wrappingClass = new Class { Name = className, Namespace = tu };
                    tu.Classes.Add(wrappingClass);
                }
                foreach (
                    MacroDefinition macro in
                        tu.PreprocessedEntities.OfType<MacroDefinition>())
                {
                    if (macro.Enumeration != null)
                        continue;

                    Field item = GenerateConstValueFromMacro(context, macro);
                    if (item == null)
                        continue;
                    wrappingClass.Fields.Add(item);
                }

            }
        }
    }
}