/// Compiled ahead of time, so a session evaluates the PROJECT's own IL calling into a native library from a package.
module Repro

open System.Runtime.InteropServices

/// SQLite's own version string, asked of the native library the package carries: it can only answer if that library loaded.
let sqliteVersion () : string =
  use connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
  connection.Open()
  use command = connection.CreateCommand()
  command.CommandText <- "select sqlite_version()"
  string (command.ExecuteScalar())

/// A native library no package ships and no machine has.
[<DllImport("sagefs_fixture_no_such_native_library")>]
extern int missingNative()

/// Calls it: the failure a user sees when the library a package should have provided is not there.
let callMissingNative () : int = missingNative ()
