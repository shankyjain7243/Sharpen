﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sharpen.Engine.Extensions.CodeDetection;
using Sharpen.Engine.SharpenSuggestions.CSharp80.NullableReferenceTypes.Suggestions;

namespace Sharpen.Engine.Analysis
{
    public abstract class BaseScopeAnalyzer : IScopeAnalyzer
    {
        
        // We want to avoid creation of a huge number of temporary Action objects
        // while invoking Parallel.Invoke().
        // That's why we create these Action objects in advance and at the beginning
        // of the analysis create just once out of them Actions that are really used in
        // the Parallel.Invoke().
        private static Action<SyntaxTree, SemanticModel, SingleSyntaxTreeAnalysisContext, ConcurrentBag<AnalysisResult>, ConcurrentBag<AnalysisResult>>[] AnalyzeSingleSyntaxTreeAndCollectResultsActions { get; } =
            SharpenAnalyzersHolder.Analyzers
                .Select(analyzer => new Action<SyntaxTree, SemanticModel, SingleSyntaxTreeAnalysisContext, ConcurrentBag<AnalysisResult>, ConcurrentBag<AnalysisResult>>((syntaxTree, semanticModel, analysisContext, results, potentialDuplicates) =>
                {
                    foreach (var analysisResult in analyzer.Analyze(syntaxTree, semanticModel, analysisContext))
                    {
                        if ((analyzer as ISharpenSuggestion) != null)
                        {
                            var suggestionLanguageVersion = Convert.ToInt64(Convert.ToDouble(((ISharpenSuggestion)analyzer).MinimumLanguageVersion));
                            if (suggestionLanguageVersion > (int)analysisContext.LanguageVersion)
                            {
                                analysisResult.IsApplicableOnCurrentLanguageVersion = false;
                            }

                        }
                        results.Add(analysisResult);
                        // TODO-IG: Remove this workaround once the whole analysis stuff is refactored.
                        if (analysisResult.Suggestion is BaseEnableNullableContextAndDeclareIdentifierAsNullableSuggestion)
                            potentialDuplicates.Add(analysisResult);
                    }
                }))
                .ToArray();

        public bool CanExecuteScopeAnalysis(out string errorMessage)
        {
            errorMessage = GetCanExecuteScopeAnalysisErrorMessage();

            if (errorMessage != null && !string.IsNullOrWhiteSpace(ScopeAnalysisHelpMessage))
                errorMessage += $"{Environment.NewLine}{Environment.NewLine}{ScopeAnalysisHelpMessage}";

            return errorMessage == null;
        }

        protected abstract string GetCanExecuteScopeAnalysisErrorMessage();
        protected abstract string ScopeAnalysisHelpMessage { get; }

        public int GetAnalysisMaximumProgress() => GetDocumentsToAnalyze().Count();

        public async Task<IEnumerable<AnalysisResult>> AnalyzeScopeAsync(IProgress<int> progress)
        {
            var analysisResults = new ConcurrentBag<AnalysisResult>();
            var potentialDuplicates = new ConcurrentBag<AnalysisResult>();
            SyntaxTree syntaxTree = null;
            SemanticModel semanticModel = null;
            SingleSyntaxTreeAnalysisContext analysisContext = null;

            var analyzeSyntaxTreeActions = AnalyzeSingleSyntaxTreeAndCollectResultsActions
                // We intentionally access the modified closure here (syntaxTree, semanticModel, analysisContext),
                // because we want to avoid creation of a huge number of temporary Action objects.
                // ReSharper disable AccessToModifiedClosure
                .Select(action => new Action(() => action(syntaxTree, semanticModel, analysisContext, analysisResults, potentialDuplicates)))
                // ReSharper restore AccessToModifiedClosure
                .ToArray();

            // Same here. We want to have just a single Action object created and called many times.
            // We intentionally do not want to use a local function here. Although its usage would be
            // semantically nicer and create exactly the same closure as the below Action, the fact that
            // we need to convert that local function to Action in the Task.Run() call means we would
            // end up in creating an additional Action object for every pass in the loop, and that's
            // exactly what we want to avoid.
            // ReSharper disable once ConvertToLocalFunction
            Action analyzeSyntaxTreeInParallel = () => Parallel.Invoke(analyzeSyntaxTreeActions);

            // WARNING: Keep the progress counter in sync with the logic behind the calculation of the maximum progress!
            int progressCounter = 0;
            foreach (var document in GetDocumentsToAnalyze())
            {
                analysisContext = new SingleSyntaxTreeAnalysisContext(document);

                syntaxTree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);

                if (!syntaxTree.BeginsWithAutoGeneratedComment())
                {
                    semanticModel = await document.GetSemanticModelAsync();

                    // Each of the actions (analysis) will operate on the same (current) syntaxTree and semanticModel.
                    await Task.Run(analyzeSyntaxTreeInParallel);
                }

                progress.Report(++progressCounter);
            }

            // TODO-IG: Fully refactor Analysis/Scope/Analyzer/Context/Result etc.
            //          and remove this terrible temporary workaround.
            var duplicatesToRemove = FindDuplicatesToRemove();

            return analysisResults.Except(duplicatesToRemove);

            IReadOnlyCollection<AnalysisResult> FindDuplicatesToRemove()
            {
                return potentialDuplicates
                    // We consider the result to be a duplicate if have the same
                    // suggestion on the same node several times.
                    // The AnalysisResult does not contain node (at the moment,
                    // who knows what the upcoming refactoring will bring us ;-))
                    // so we will see if the file name and the position are the same.
                    .GroupBy(result => new { result.Suggestion, result.FilePath, result.Position })
                    .Where(group => group.Count() > 1)
                    // Just leave the first one so far and mark the rest as those to be removed.
                    // This is all a temporary workaround after all :-)
                    .SelectMany(group => group.Skip(1))
                    .ToList();
            }
        }

        // The iteration over documents to analyze happens few times:
        // - In the checks in the CanExecuteScopeAnalysis() of the base classes.
        // - In the GetAnalysisMaximumProgress() where all are iterated to get the count.
        // - In the AnalyzeScopeAsync() of course.
        // This iteration redundancy is not a performance issue.
        protected abstract IEnumerable<Document> GetDocumentsToAnalyze();

        protected static bool ProjectIsCSharpProject(Project project)
        {
            return project.Language == "C#";
        }

        protected static bool DocumentShouldBeAnalyzed(Document document)
        {
            return document.SupportsSyntaxTree &&
                   document.SupportsSemanticModel && 
                   !document.IsGenerated() &&
                   !document.IsGeneratedAssemblyInfo();
        }
    }
}