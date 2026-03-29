using Xunit;

namespace Trignis.Tests.Helpers;

/// <summary>
/// Marks tests that must not run concurrently with each other because they
/// temporarily mutate Environment.CurrentDirectory (SQLite file-path tests).
/// </summary>
[CollectionDefinition("SqliteTests", DisableParallelization = true)]
public sealed class SqliteTestsCollection;
