using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Underscore-delimited xUnit method names are retained as readable behavioral test descriptions.",
    Scope = "namespaceanddescendants",
    Target = "~N:TodoApp.Eval.Tests")]
