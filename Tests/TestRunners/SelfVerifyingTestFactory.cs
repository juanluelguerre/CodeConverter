﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CodeConverter.Tests.Compilation;
using CodeConverter.Tests.CSharp;
using ICSharpCode.CodeConverter;
using ICSharpCode.CodeConverter.CSharp;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Sdk;

namespace CodeConverter.Tests.TestRunners
{
    internal class SelfVerifyingTestFactory
    {
        /// <summary>
        /// Returns facts which when executed, ensure the source Fact succeeds, then convert it, and ensure the target Fact succeeds too.
        /// </summary>
        public static IEnumerable<NamedFact> GetSelfVerifyingFacts<TSourceCompiler, TTargetCompiler, TLanguageConversion>(string testFilepath)
            where TSourceCompiler : ICompiler, new() where TTargetCompiler : ICompiler, new() where TLanguageConversion : ILanguageConversion, new()
        {
            var sourceFileText = File.ReadAllText(testFilepath);
            var sourceCompiler = new TSourceCompiler();
            var syntaxTree = sourceCompiler.CreateTree(sourceFileText).WithFilePath(Path.GetFullPath(testFilepath));
            var compiledSource = sourceCompiler.AssemblyFromCode(syntaxTree, AdditionalReferences);
            var runnableTestsInSource = XUnitFactDiscoverer.GetNamedFacts(compiledSource).ToList();
            Assert.NotEmpty(runnableTestsInSource);

            return GetSelfVerifyingFacts<TTargetCompiler, TLanguageConversion>(sourceFileText, runnableTestsInSource);
        }

        private static IEnumerable<NamedFact> GetSelfVerifyingFacts<TTargetCompiler, TLanguageConversion>(string sourceFileText,
                List<NamedFact> runnableTestsInSource) where TTargetCompiler : ICompiler, new()
            where TLanguageConversion : ILanguageConversion, new()
        {
            // Lazy to avoid confusing test runner on error, but also avoid calculating multiple times
            var conversionResultAsync = new AsyncLazy<ConversionResult>(() =>
                ProjectConversion.ConvertText<TLanguageConversion>(sourceFileText, new TextConversionOptions(DefaultReferences.NetStandard2))
            );

            var runnableTestsInTarget = new AsyncLazy<Dictionary<string, NamedFact>>(async () => GetConvertedNamedFacts<TTargetCompiler>(runnableTestsInSource,
                await conversionResultAsync.GetValueAsync()));

            return runnableTestsInSource.Select(sourceFact =>
                new NamedFact(sourceFact.Name, async () =>
                {
                    try
                    {
                        await sourceFact.Execute();
                    }
                    catch (TargetInvocationException ex)
                    {
                        throw new XunitException(
                            $"Source test failed, ensure the source is correct for \"{sourceFact.Name}\": {(ex.InnerException ?? ex)}");
                    }

                    try {
                        var test = await runnableTestsInTarget.GetValueAsync();
                        await test[sourceFact.Name].Execute();
                    }
                    catch (TargetInvocationException ex) {
                        var conversionResult = await conversionResultAsync.GetValueAsync();
                        throw new XunitException(
                            $"Converted test failed, the conversion is incorrect for \"{sourceFact.Name}\": {(ex.InnerException ?? ex)}\r\nConverted Code: {conversionResult.ConvertedCode ?? conversionResult.GetExceptionsAsString()}");
                    }
                })
            );
        }

        private static Dictionary<string, NamedFact> GetConvertedNamedFacts<TTargetCompiler>(List<NamedFact> runnableTestsInSource, ConversionResult convertedText)
            where TTargetCompiler : ICompiler, new()
        {
            string code = convertedText.ConvertedCode;
            var targetCompiler = new TTargetCompiler();
            var compiledTarget = targetCompiler.AssemblyFromCode(targetCompiler.CreateTree(code), AdditionalReferences);
            var runnableTestsInTarget = XUnitFactDiscoverer.GetNamedFacts(compiledTarget).ToDictionary(f => f.Name);

            Assert.Equal(runnableTestsInSource.Select(f => f.Name), runnableTestsInTarget.Keys);
            return runnableTestsInTarget;
        }

        private static Assembly[] AdditionalReferences =>
            new[] {typeof(Assert).Assembly, typeof(FactAttribute).Assembly};
    }
}