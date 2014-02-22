﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CppSharp.AST;
using CppSharp.Generators;
using CppSharp.Passes;
using FFmpegBindings;

namespace CppSharp
{
    internal static class Program
    {
        public static void Main(string[] args)
        {
//            GenerateLibrary(new WinApi());
//            Environment.Exit(0);

            GenerateFFmpeg();
        }

        private static void GenerateFFmpeg()
        {
//            var versionString = "2.1.3";
            string versionString = "1.0.7";
            var ffmpegInstallDir = new DirectoryInfo(@"C:\WORK\REPOS-SC\FFmpeg_bindings\ffmpeg\" + versionString);
            var outputDir = new DirectoryInfo(@"C:\WORK\REPOS-SC\FFmpeg_bindings\src\" + versionString);

            var avutilLib = new FFmpegSubLibrary(ffmpegInstallDir, "avutil", "avutil-if-52.dll", outputDir);
            var avcodecLib = new FFmpegSubLibrary(ffmpegInstallDir, "avcodec", "avcodec-if-55.dll", outputDir,
                new List<string>
                {
                    "old_codec_ids.h",
                    "dxva2.h",
                    "vda.h",
                    "vdpau.h",
                    "xvmc.h"
                }, new List<IComplexLibrary> {avutilLib});

            var avformatLib = new FFmpegSubLibrary(ffmpegInstallDir, "avformat", "avformat-if-55.dll", outputDir, null,
                new List<IComplexLibrary> {avutilLib, avcodecLib});

            var swresampleLib = new FFmpegSubLibrary(ffmpegInstallDir, "swresample", "swresample-if-0.dll", outputDir,
                null, new List<IComplexLibrary> {avutilLib});

            var swscaleLib = new FFmpegSubLibrary(ffmpegInstallDir, "swscale", "swscale-if-2.dll", outputDir, null,
                new List<IComplexLibrary> {avutilLib});

            var avfilterLib = new FFmpegSubLibrary(ffmpegInstallDir, "avfilter", "avfilter-if-3.dll", outputDir, null,
                new List<IComplexLibrary> {avutilLib, swresampleLib, swscaleLib, avcodecLib, avformatLib});

            var avdeviceLib = new FFmpegSubLibrary(ffmpegInstallDir, "avdevice", "avdevice-if-55.dll", outputDir);

            GenerateComplexLibraries(new List<IComplexLibrary>
            {
                avcodecLib,
                avformatLib,
                swresampleLib,
                swscaleLib,
                avfilterLib,
                avdeviceLib,
                avutilLib,
            });
        }

        private static void GenerateComplexLibraries(IList<IComplexLibrary> complexLibraries)
        {
            var log = new TextDiagnosticPrinter();

            // sort topoligically (by dependencies)
            IEnumerable<IComplexLibrary> sorted = complexLibraries.TSort(l => l.DependentLibraries);
            var drivers = new List<S>();

            log.EmitMessage("Parsing libraries...");
            foreach (IComplexLibrary lib in sorted)
            {
                List<Driver> dependents = drivers.Select(s => s.Driver).ToList();
                drivers.Add(new S
                {
                    Library = lib,
                    Driver = GenerateLibrary(log, lib, dependents),
                    Dependents = dependents,
                });
            }

            log.EmitMessage("Postprocess ...");
            var generated = new List<TranslationUnit>();
            foreach (S s in drivers)
            {
                IComplexLibrary library = s.Library;
                Driver driver = s.Driver;

                library.Postprocess(driver, driver.ASTContext, s.Dependents.Select(d => d.ASTContext));
            }

            log.EmitMessage("Generating ...");
            foreach (S s in drivers)
            {
                Driver driver = s.Driver;

                List<GeneratorOutput> outputs = driver.GenerateCode(generated);

                foreach (GeneratorOutput output in outputs)
                {
                    foreach (GeneratorOutputPass pass in driver.GeneratorOutputPasses.Passes)
                    {
                        pass.Driver = driver;
                        pass.VisitGeneratorOutput(output);
                    }
                }

                driver.WriteCode(outputs);

                generated.AddRange(outputs.Select(t => t.TranslationUnit));
            }
        }

        private static Driver GenerateLibrary(TextDiagnosticPrinter log, IComplexLibrary library,
            IList<Driver> dependentLibraries)
        {
            var options = new DriverOptions
            {
                TargetTriple = "i686-pc-win32",
                //            TargetTriple = "x86_64-pc-win64",
                Gnu99Mode = true,
                Verbose = false,
            };

            var driver = new Driver(options, log);
            log.Verbose = driver.Options.Verbose;

            library.Setup(driver);
            driver.Setup();

            foreach (Driver dependentLibDriver in dependentLibraries)
            {
                foreach (TranslationUnit tu in dependentLibDriver.ASTContext.TranslationUnits)
                {
                    driver.ASTContext.TranslationUnits.Add(tu);
                }
            }

            log.EmitMessage("Parsing libraries...");

            if (!driver.ParseLibraries())
                return driver;

            if (!options.Quiet)
                log.EmitMessage("Indexing library symbols...");

            driver.Symbols.IndexSymbols();

            if (!options.Quiet)
                log.EmitMessage("Parsing code...");

            if (!driver.ParseCode())
                return driver;

            if (!options.Quiet)
                log.EmitMessage("Processing code...");

//            driver.ASTContext.ResolveUnifyIncompleteClassDeclarationsFromSubLibs(dependentLibraries);

            library.Preprocess(driver, driver.ASTContext, dependentLibraries.Select(d => d.ASTContext));

            driver.SetupPasses(library);

            driver.ProcessCode();

            return driver;
        }

        private struct S
        {
            public Driver Driver { get; set; }
            public List<Driver> Dependents { get; set; }
            public IComplexLibrary Library { get; set; }
        }
    }

    internal static class TopologicalSort
    {
        /// <summary>
        ///     Topological sort (least dependent first)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="source"></param>
        /// <param name="dependencies"></param>
        /// <returns></returns>
        public static IEnumerable<T> TSort<T>(this IEnumerable<T> source, Func<T, IEnumerable<T>> dependencies)
        {
            var sorted = new List<T>();
            var visited = new HashSet<T>();

            foreach (T item in source)
                Visit(item, visited, sorted, dependencies);

            return sorted;
        }

        private static void Visit<T>(T item, HashSet<T> visited, List<T> sorted,
            Func<T, IEnumerable<T>> dependencies)
        {
            if (visited.Contains(item)) return;

            visited.Add(item);

            foreach (T dep in dependencies(item))
                Visit(dep, visited, sorted, dependencies);

            sorted.Add(item);
        }
    }
}