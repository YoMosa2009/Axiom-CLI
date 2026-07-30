using System;
using System.Reflection;
using Axiom.Core.Tools;
using Xunit;

namespace Axiom.Core.Tests.Tools
{
    // Regression coverage for query-construction and relevance-scoring bugs found by live
    // reproduction against the real search providers. These tests are deliberately network-free
    // (they call the private query-building/scoring internals via reflection) so they run
    // deterministically in CI without depending on DuckDuckGo/Bing being reachable.
    public class WebSearchServiceTests
    {
        private static object InvokePrivateStatic(string methodName, params object?[] args)
        {
            var method = typeof(WebSearchService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            return method!.Invoke(null, args)!;
        }

        [Fact]
        public void BuildStrategicSearchQuery_DoesNotLeakWhQuestionWordsIntoTheQuery()
        {
            // Reproduction: "who is the current CEO of OpenAI" previously produced the literal
            // search query "CEO OpenAI who latest current 2026 article" -- "who" leaked in because
            // TopicShiftStopWords (used by the keyword extractor that feeds the query) was missing
            // several WH-words that the other three stopword sets already had.
            var svc = new WebSearchService();
            string query = svc.BuildStrategicSearchQuery("who is the current CEO of OpenAI");

            foreach (string whWord in new[] { "who", "why", "where", "when", "which" })
            {
                Assert.DoesNotContain(
                    whWord,
                    query.Split(' '),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void BuildStrategicSearchQuery_PreservesShortVersionNumbers()
        {
            // Reproduction: "dotnet 10 release notes" produced the query "dotnet release notes
            // version changelog" -- "10" was silently dropped by three independent copies of the
            // same "shorter than 3 chars is noise" filter, collapsing a version-specific query down
            // to a generic one.
            var svc = new WebSearchService();
            string query = svc.BuildStrategicSearchQuery("dotnet 10 release notes");

            Assert.Contains("10", query.Split(' '));
        }

        [Fact]
        public void ScoreResult_RanksResultMentioningTheNamedEntityAboveOnesThatDoNot()
        {
            // Reproduction: for "who is the current CEO of OpenAI", a generic Wikipedia "Chief
            // executive officer" definition page and an unrelated county-jail-inmate page (which
            // happened to match the word "current") both out-competed/nearly-matched a genuinely
            // on-topic OpenAI result, because query-embellishment words ("latest", "current",
            // "article") scored the same as the actual named entity in the prompt.
            var intent = InvokePrivateStatic("BuildSearchIntent", "who is the current CEO of OpenAI");

            var searchResultType = typeof(WebSearchService).GetNestedType("SearchResult", BindingFlags.NonPublic)!;
            var ctor = searchResultType.GetConstructors()[0];

            object OnTopic() => ctor.Invoke(new object?[]
            {
                "Sam Altman - OpenAI", "Sam Altman is the CEO of OpenAI, an AI research company.",
                "https://openai.com/our-structure/", 0, null
            });
            object GenericDefinition() => ctor.Invoke(new object?[]
            {
                "Chief executive officer - Wikipedia", "The CEO is the highest-ranking executive in a company.",
                "https://en.wikipedia.org/wiki/Chief_executive_officer", 1, null
            });
            object OffTopic() => ctor.Invoke(new object?[]
            {
                "Current Inmates - Polk Inmates", "Individuals obtaining information from this web site should verify accuracy.",
                "https://polkinmates.polkcountyiowa.gov/Inmates/Current", 2, null
            });

            int onTopicScore = (int)InvokePrivateStatic("ScoreResult", OnTopic(), intent);
            int genericScore = (int)InvokePrivateStatic("ScoreResult", GenericDefinition(), intent);
            int offTopicScore = (int)InvokePrivateStatic("ScoreResult", OffTopic(), intent);

            Assert.True(onTopicScore > genericScore, $"on-topic ({onTopicScore}) should outscore generic definition ({genericScore})");
            Assert.True(genericScore > offTopicScore, $"generic definition ({genericScore}) should outscore fully off-topic ({offTopicScore})");
        }
    }
}
