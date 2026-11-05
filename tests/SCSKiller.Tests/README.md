# HoYo / Unity reader regression tests

The public checkout had no original C# test tree. This project adds focused reader tests, with synthetic serialized
files and minimal containers. Synthetic containers exercise parsing and grouping, not executable GPU programs.

```powershell
dotnet test tests/SCSKiller.Tests/SCSKiller.Tests.csproj -c Release --filter 'Needs!=Game'
```

The optional integration test is marked `Needs=Game` and is skipped without `SCSK_GENSHIN_TEST_ROOT`. It opens a local
installation or prepared fixture for reading, indexes resources and checks bytecode identity. It never launches the
game or runs a GPU warmer. Set variables in the invoking shell:

| Variable | Purpose |
|---|---|
| `SCSK_GENSHIN_TEST_ROOT` | Installation/fixture root containing YuanShen.exe or GenshinImpact.exe and its Data folder |
| `SCSK_GENSHIN_TEST_OUTPUT` | Caller-selected workspace output folder; default is under the test binary folder |
| `SCSK_GENSHIN_OODLE_LIBRARY` | Optional local copy of the project's pinned native codec; copied to the test output and verified by Codecs |
| `SCSK_GENSHIN_EXPECTED_INDEX` | Optional prior analysis index JSON (`shaders[].Sha1`) for exact set comparison; also requests identity checks of every shader |
| `SCSK_GENSHIN_BUILD_PLAN=1` | Also build/materialize a DX11 plan using simulated NVIDIA capabilities; no GPU execution |

```powershell
dotnet test tests/SCSKiller.Tests/SCSKiller.Tests.csproj -c Release --filter 'Needs=Game'
```

Without an expected index, the complete installation is indexed but materialization checks up to eight shaders per
model. Source content is never included in this test project; do not commit local game fixtures or binaries.

Star Rail / Zenless integration verification uses `FullyQualifiedName~HoyoIntegrationTests`. Set `SCSK_HOYO_ROOT` to
the installed root, `SCSK_HOYO_OUTPUT` to a workspace-only output directory, and `SCSK_HOYO_OODLE` to the pinned codec.
`SCSK_HOYO_BASELINE` names the stock-reader JSON (`shaders[].Sha1`); `SCSK_HOYO_REFERENCE` names the independent
packed-reader JSON (`shaders[].sha1`). The product index must include their complete union. The test checks sample
hash identities and materializes a DX11 plan with simulated NVIDIA capabilities. It does not execute GPU work.
Hull/domain pairs count as one plan item, so raw bytecode counts need not equal plan item counts.
