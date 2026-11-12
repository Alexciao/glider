using System.Diagnostics.CodeAnalysis;

// Fakes complete synchronously or on manual time; threading TestContext tokens through every call adds noise only.
[assembly: SuppressMessage("Usage", "xUnit1051", Scope = "namespaceanddescendants", Target = "~N:Glider.Core.Tests.Services")]
