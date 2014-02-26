using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
//            Generate(new WinApi());
//            Environment.Exit(0);

            GenerateFFmpeg();
        }

        private static void GenerateFFmpeg()
        {
//            var versionString = "2.1.3";
            var versionString = "1.0.7";
            var ffmpegInstallDir = new DirectoryInfo(@"C:\WORK\REPOS-SC\FFmpeg_bindings\ffmpeg\" + versionString);
            var outputDir = new DirectoryInfo(@"C:\WORK\REPOS-SC\FFmpeg_bindings\src\" + versionString);

            var avutilLib = new FFmpegSubLibrary(ffmpegInstallDir, "avutil", "avutil-if-52.dll", outputDir);
            var avutilDriver = Generate(avutilLib);
            var avcodecLib = new FFmpegSubLibrary(ffmpegInstallDir, "avcodec", "avcodec-if-55.dll", outputDir, new List<string>
            {
                "old_codec_ids.h",
                "dxva2.h",
                "vda.h",
                "vdpau.h",
                "xvmc.h"
            }, avutilDriver);
            var avcodecDriver = Generate(avcodecLib);

            var avformatLib = new FFmpegSubLibrary(ffmpegInstallDir, "avformat", "avformat-if-55.dll", outputDir, null,
                avutilDriver, avcodecDriver);
            var avformatDriver = Generate(avformatLib);

            var swresampleLib = new FFmpegSubLibrary(ffmpegInstallDir, "swresample", "swresample-if-0.dll", outputDir,
                null, avutilDriver);
            var swresampleDriver = Generate(swresampleLib);

            var swscaleLib = new FFmpegSubLibrary(ffmpegInstallDir, "swscale", "swscale-if-2.dll", outputDir, null,
                avutilDriver);
            var swscaleDriver = Generate(swscaleLib);

            var avfilterLib = new FFmpegSubLibrary(ffmpegInstallDir, "avfilter", "avfilter-if-3.dll", outputDir, null,
                avutilDriver, swresampleDriver, swscaleDriver, avcodecDriver, avformatDriver);
            var avfilterDriver = Generate(avfilterLib);

            var avdeviceLib = new FFmpegSubLibrary(ffmpegInstallDir, "avdevice", "avdevice-if-55.dll", outputDir);
            var avdeviceDriver = Generate(avdeviceLib);
        }

        /// <summary>
        ///     Some of the code from ConsoleDriver.Run
        /// </summary>
        /// <param name="library"></param>
        private static Driver Generate(ILibrary library)
        {
            var options = new DriverOptions
            {
                TargetTriple = "i686-pc-win32",
                //            TargetTriple = "x86_64-pc-win64",
                Gnu99Mode = true,
                Verbose = false,
            };

            var log = new TextDiagnosticPrinter();
            var driver = new Driver(options, log);
            log.Verbose = driver.Options.Verbose;

            library.Setup(driver);
            driver.Setup();

            if (!options.Quiet)
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

            library.Preprocess(driver, driver.ASTContext);

            driver.SetupPasses(library);

            driver.ProcessCode();
            library.Postprocess(driver, driver.ASTContext);

            if (!options.Quiet)
                log.EmitMessage("Generating code...");

            List<GeneratorOutput> outputs = driver.GenerateCode();

            foreach (GeneratorOutput output in outputs)
            {
                foreach (GeneratorOutputPass pass in driver.GeneratorOutputPasses.Passes)
                {
                    pass.Driver = driver;
                    pass.VisitGeneratorOutput(output);
                }
            }

            driver.WriteCode(outputs);
            if (driver.Options.IsCSharpGenerator)
                driver.CompileCode();

            //            ConsoleDriver.Run(library);

            return driver;
        }
    }
}