using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CppSharp;
using CppSharp.AST;
using Type = CppSharp.AST.Type;

namespace FFmpegBindings
{
    internal class FFmpegSubLibrary : ILibrary
    {
        private readonly IEnumerable<string> _filesToIgnore;
        public string LibraryName { get; private set; }
        public string DllName { get; private set; }
        private readonly DirectoryInfo _outputDir;
        private readonly Driver[] _dependentLibDrivers;
        private readonly DirectoryInfo _includeDir;
        public string LibraryNameSpace { get; private set; }

        public FFmpegSubLibrary(DirectoryInfo includeDir, string libraryName, string dllName, DirectoryInfo outputDir, IEnumerable<string> filesToIgnore = null, params Driver[] dependentLibDrivers)
        {
            _includeDir = includeDir;
            if (!_includeDir.Exists)
                throw new DirectoryNotFoundException(_includeDir.FullName);

            LibraryName = libraryName;
            LibraryNameSpace = "lib" + LibraryName;
            DllName = dllName;

            _outputDir = outputDir;
            _dependentLibDrivers = dependentLibDrivers;
            _filesToIgnore = filesToIgnore ?? Enumerable.Empty<string>();
        }

        public virtual void Preprocess(Driver driver, ASTContext ctx)
        {
            // ignore the other ffmpeg sublibraries (e.g. don't include avutil stuff when generating for avcodec)
//            foreach (var dependentLibDriver in _dependentLibDrivers)
//            {
//                foreach (var translationUnit in dependentLibDriver.ASTContext.TranslationUnits)
//                {
//                    ctx.TranslationUnits.Add(translationUnit);
//                }
//            }
            foreach (DirectoryInfo subLibDir in _includeDir.GetDirectories())
            {
                if (subLibDir.Name.Contains(LibraryName))
                    continue;

                foreach (FileInfo headerFile in subLibDir.GetFiles())
                {
                    foreach (
                        TranslationUnit unit in ctx.TranslationUnits.FindAll(m => m.FilePath == headerFile.FullName))
                    {
                        unit.IsGenerated = false;
                        unit.ExplicityIgnored = true;
                    }
                }
            }

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
            ctx.ResolveUnifyIncompleteClassDeclarationsFromSubLibs(_dependentLibDrivers);
//            ctx.ResolveUnifyIncompleteClassDeclarations();
            // it's not possible to handle va_list using p/invoke
            ctx.IgnoreFunctionsWithParameterTypeName("va_list");
            ctx.GenerateClassWithConstValuesFromMacros(LibraryName);
        }

        public virtual void Postprocess(Driver driver, ASTContext lib)
        {
            foreach (var tu in lib.TranslationUnits)
            {
                if (tu.Classes.Count == 1 && tu.Classes[0].Name == LibraryNameSpace)
                    continue;

                var wrappingClass = new Class()
                {
                    Name = LibraryNameSpace,
                    Namespace =  tu
                };

                wrappingClass.Classes.AddRange(tu.Classes);
                foreach (var decl in wrappingClass.Classes)
                {
                    decl.Namespace = wrappingClass;
                }
                tu.Classes.Clear();
                tu.Classes.Add(wrappingClass);

                wrappingClass.Functions.AddRange(tu.Functions);
                foreach (var decl in wrappingClass.Functions)
                {
                    decl.Namespace = wrappingClass;
                }
                tu.Functions.Clear();

                wrappingClass.Enums.AddRange(tu.Enums);
                foreach (var decl in wrappingClass.Enums)
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
            foreach (var dependentLibrary in _dependentLibDrivers)
            {
                driver.Options.DependentNameSpaces.Add(dependentLibrary.Options.OutputNamespace);
            }
        }

        public virtual void SetupPasses(Driver driver)
        {
        }

        protected virtual bool ShouldIncludeHeader(FileInfo headerFileName)
        {
            if (_filesToIgnore.Contains(headerFileName.Name))
                return false;
            return true;
        }
    }

    public static class X
    {
        public static Field GenerateConstValueFromMacro(this ASTContext context,
            MacroDefinition macro)
        {
            long val;
            if (!ParseToNumber(macro.Expression, out val))
                return null;
            var valueType = new QualifiedType(new BuiltinType(PrimitiveType.Int64))
            {
                Qualifiers = new TypeQualifiers() {IsConst = true}
            };
            var item = new Field
            {
                Name = macro.Name,
                DebugText = macro.DebugText,
                Access = AccessSpecifier.Public,
                Value =
                    new BuiltinValue()
                    {
                        BuiltinType = new BuiltinType(PrimitiveType.UInt64),
                        Expression = macro.Expression,
                        Value = val,
                    },
                QualifiedType = valueType
            };

            return item;
        }

        private static bool ParseToNumber(string num, out long val)
        {
            if (num.StartsWith("0x", StringComparison.CurrentCultureIgnoreCase))
            {
                num = num.Substring(2);

                return long.TryParse(num, NumberStyles.HexNumber,
                    CultureInfo.CurrentCulture, out val);
            }

            return long.TryParse(num, out val);
        }

        public static void GenerateClassWithConstValuesFromMacros(this ASTContext context, string className)
        {
            foreach (var unit in context.TranslationUnits)
            {
                var @class = new Class { Name = className };
                foreach (var macro in unit.PreprocessedEntities.OfType<MacroDefinition>().Where(m => ((TranslationUnit)m.Namespace).FilePath == unit.FilePath))
                {
                    if (macro.Enumeration != null)
                        continue;

                    var item = GenerateConstValueFromMacro(context, macro);
                    if (item == null)
                        continue;
                    @class.Fields.Add(item);
                }

                if(@class.Fields.Any())
                {
                    unit.Classes.Add(@class);
                }
            }
        }
    }

}