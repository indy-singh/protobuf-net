﻿using Google.Protobuf.Reflection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ProtoBuf.Schemas
{
    [Trait("kind", "schema")]
    public class SchemaTests
    {
        private ITestOutputHelper _output;

        const string SchemaPath = "Schemas";
        public static IEnumerable<object[]> GetSchemas()
            => from file in Directory.GetFiles(SchemaPath, "*.proto", SearchOption.AllDirectories)
               select new object[] { Regex.Replace(file.Replace('\\', '/'), "^Schemas/", "") };

        [Fact]
        public void BasicCompileClientWorks()
        {
            var result = ProtoBuf.CSharpCompiler.Compile(new CodeFile("my.proto", "syntax=\"proto3\"; message Foo {}"));
            Assert.Equal(0, result.Errors.Length);
            Assert.True(result.Files.Single().Text.StartsWith("// This file was generated by a tool;"));
        }
        [Theory]
        [MemberData(nameof(GetSchemas))]
        public void CompareProtoToParser(string path)
        {
            var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), SchemaPath);
            _output.WriteLine(schemaPath);

            var protocBinPath = Path.Combine(schemaPath, Path.ChangeExtension(path, "protoc.bin"));
            int exitCode;
            using (var proc = new Process())
            {
                var psi = proc.StartInfo;
                psi.FileName = "protoc";
                psi.Arguments = $"--descriptor_set_out={protocBinPath} {path}";
                psi.RedirectStandardError = psi.RedirectStandardOutput = true;
                psi.UseShellExecute = false;
                psi.WorkingDirectory = schemaPath;
                proc.Start();
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(5000))
                {
                    try { proc.Kill(); } catch { }
                }
                exitCode = proc.ExitCode;
                string err = "", @out = "";
                if (stdout.Wait(1000)) @out = stdout.Result;
                if (stderr.Wait(1000)) err = stderr.Result;

                if (!string.IsNullOrWhiteSpace(@out))
                {
                    _output.WriteLine("stdout: ");
                    _output.WriteLine(@out);
                }
                if (!string.IsNullOrWhiteSpace(err))
                {
                    _output.WriteLine("stderr: ");
                    _output.WriteLine(err);
                }
            }

            FileDescriptorSet set;
            string protocJson = null, jsonPath;
            if (exitCode == 0)
            {
                using (var file = File.OpenRead(protocBinPath))
                {
                    set = Serializer.Deserialize<FileDescriptorSet>(file);
                    protocJson = JsonConvert.SerializeObject(set, Formatting.Indented);
                    jsonPath = Path.Combine(schemaPath, Path.ChangeExtension(path, "protoc.json"));
                    File.WriteAllText(jsonPath, protocJson);
                }
            }



            set = new FileDescriptorSet();
            
            set.AddImportPath(schemaPath);
            set.Add(path, includeInOutput: true);

            set.Process();


            var parserJson = JsonConvert.SerializeObject(set, Formatting.Indented);
            jsonPath = Path.Combine(schemaPath, Path.ChangeExtension(path, "parser.json"));
            File.WriteAllText(jsonPath, parserJson);

            var errors = set.GetErrors();
            Exception genError = null;

            try {
                foreach (var file in CSharpCodeGenerator.Default.Generate(set))
                {
                    File.WriteAllText(Path.Combine(schemaPath, file.Name), file.Text);
                }
            }
            catch (Exception ex)
            {
                genError = ex;
                _output.WriteLine(ex.Message);
                _output.WriteLine(ex.StackTrace);
            }

            var parserBinPath = Path.Combine(schemaPath, Path.ChangeExtension(path, "parser.bin"));
            using (var file = File.Create(parserBinPath))
            {
                Serializer.Serialize(file, set);
            }

            
            if (errors.Any())
            {
                _output.WriteLine("Parser errors:");
                foreach (var err in errors) _output.WriteLine(err.ToString());
            }

            _output.WriteLine("Protoc exited with code " + exitCode);

            var errorCount = errors.Count(x => x.IsError);
            if (exitCode == 0)
            {
                Assert.Equal(0, errorCount);
            }
            else
            {
                Assert.NotEqual(0, errorCount);
            }



            var parserHex = BitConverter.ToString(File.ReadAllBytes(parserBinPath));
            File.WriteAllText(Path.ChangeExtension(parserBinPath, "parser.hex"), parserHex);

            if (exitCode == 0)
            {
                var protocHex = BitConverter.ToString(File.ReadAllBytes(protocBinPath));
                File.WriteAllText(Path.ChangeExtension(protocBinPath, "protoc.hex"), protocHex);

                // compare results
                Assert.Equal(protocJson, parserJson);
                Assert.Equal(protocHex, parserHex);
            }



            Assert.Null(genError);
        }

        public SchemaTests(ITestOutputHelper output) => _output = output;

    }
}
