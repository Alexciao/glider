using System.Diagnostics.CodeAnalysis;

// The fakes complete on manual time or in memory; threading TestContext tokens through every call adds noise only.
[assembly: SuppressMessage("Usage", "xUnit1051", Scope = "namespaceanddescendants", Target = "~N:Glider.Core.Tests.Art")]
