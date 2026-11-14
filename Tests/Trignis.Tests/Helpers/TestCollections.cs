using Xunit;

namespace Trignis.Tests.Helpers;

/// <summary>
/// Collection for tests that mutate Environment.CurrentDirectory (SQLite file-path tests). Tests in the collection run sequentially.
/// </summary>
[CollectionDefinition("SqliteTests", DisableParallelization = true)]
public sealed class SqliteTestsCollection;
