module SageFs.Tests.DatastarBundleIntegrityTests

open System.Reflection
open System.Security.Cryptography
open Expecto
open Expecto.Flip

/// Enforce the pinned Datastar bundle (roast-8 §1/§7). The dashboard serves an
/// embedded, self-hosted `datastar.js` instead of fetching an unversioned CDN
/// branch; `Dashboard.datastarBundleSha256` records the reviewed bytes' hash.
/// This test HASHES the actual embedded resource and asserts it equals that
/// constant, so the served bundle can never silently drift from the reviewed
/// one — upgrading Datastar must replace the file AND update the constant in one
/// reviewed change. Without this, the "pin" was only a comment.
let private embeddedBundleSha256 () =
  let asm = Assembly.Load "SageFs"
  use stream = asm.GetManifestResourceStream("SageFs.datastar.js")
  Expect.isNotNull "the SageFs.datastar.js resource must be embedded in the SageFs assembly" stream
  use sha = SHA256.Create()
  sha.ComputeHash stream
  |> Array.map (fun b -> b.ToString("X2"))
  |> String.concat ""

[<Tests>]
let datastarBundleIntegrityTests =
  testList "Datastar bundle integrity (pinned, self-hosted)" [

    testCase "the embedded datastar.js hashes to the recorded pin" <| fun _ ->
      embeddedBundleSha256 ()
      |> Expect.equal
        "the served Datastar bundle drifted from Dashboard.datastarBundleSha256 — if this was an intentional Datastar upgrade, update datastar.js AND that constant together; otherwise the bundle was tampered with"
        SageFs.Server.Dashboard.datastarBundleSha256

    testCase "the recorded pin is a well-formed uppercase SHA-256 hex" <| fun _ ->
      let pin = SageFs.Server.Dashboard.datastarBundleSha256
      pin.Length |> Expect.equal "SHA-256 hex is 64 chars" 64
      pin
      |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))
      |> Expect.isTrue "the pin must be uppercase hex (matches ToString(\"X2\") output)"
  ]
