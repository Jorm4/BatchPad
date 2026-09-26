# BatchPad — Design

Status: the MVP (phases 1–7, §10) is implemented; later phases are design only.

## 1. Purpose

BatchPad is a Windows desktop app that organises and runs a project's scripts.
It replaces "open a terminal, cd somewhere, remember the flags" with a tree of
named scripts, a parameter form and a live output panel.

A typical multi-app game repository has scripts like these, and they show
what the app has to handle:

| Pattern seen | What BatchPad needs |
|---|---|
| `build.bat [--release\|--final\|--asan] [--games\|--tests] [Name...]`, with usage documented in a `rem` header | Choice/flag/text parameters; import the description from the header comment |
| Python tools with `argparse` (`--jobs N`, `--gated`, positional filters) | The same parameter model; optionally import from `--help` |
| `serve_web.bat [port]` / `stop_web.bat [port]` | Long-running processes, start/stop pairs sharing a parameter, open-URL action |
| `package_web.bat <App>` calls `build_web.bat <App>` | Parameters whose choices are *discovered* (list of app names); sequences |
| Build scripts that exist because grepping logs for `: error ` misses `LNK1168` | Success means exit code 0, never "no error text seen" |
| `generate.bat` removed its `pause` because it hangs unattended runs | Captured runs get a closed stdin so `pause` returns immediately |
| `build_locked.ps1` serialises builds that share an output directory | Named locks: runs sharing a lock queue instead of colliding |

### Goals

1. One place to find and run every script of a project, organised in a tree.
2. **No JSON required.** A script dropped into a script folder appears
   automatically, with its name, description and parameters detected. Every
   setting (parameters, choices, workflows, links, shared pickers) can be
   edited in the app. JSON is the storage format, not the user interface.
3. Per-project workspaces plus a global library shared across projects.
4. Personal favourites ("My Scripts") made by drag and drop, with customised
   parameters, never touching the shared config.
5. Workflows that chain scripts into a custom pipeline, runnable by hand or
   on a schedule.
6. Trustworthy results: live output, exit codes, run history.
7. Every build and packaging script of a multi-app game repository runs from
   BatchPad (§11). So does building, running, web-serving and packaging any
   of its apps, and running its tests, benchmarks, gameplay scenarios and QA
   crawls. This is the acceptance test for the MVP.
8. Standalone and shareable: one self-contained exe with no knowledge of, or
   dependency on, any project that uses it.

### Non-goals

- Replacing the scripts. BatchPad runs them; it does not become a build system.
- Cross-platform. Windows only, so Windows-native UI and behaviour are fine.
- Remote execution, CI and multi-machine orchestration. Scheduling is local:
  runs happen on this machine, as this user.

## 2. Concepts

**Script definition.** A named entry that points at a file (or a command) and
says how to run it: runner, working directory, fixed arguments, parameters,
environment, and behaviour flags (long-running, confirm, lock, and so on).

**Workspace.** A project's `batchpad.json`, normally committed to that project's
repository. Its script tree is shared by everyone who works on the project.
Relative paths resolve against the directory containing the file.

**Global library.** Per-user script definitions that appear in every workspace
(`%APPDATA%\BatchPad\global.json`). It can import further library files, so a
team can share one from a network drive or a git checkout.

**My Scripts.** The user's own tree for the current workspace. Each entry either
references a shared script and overrides parts of it (a *customisation*), or
is a complete script definition of its own. Folders are free-form.

**Workflow.** An ordered list of steps built in the app, where a step can be a
parallel group or a for-each. Each step runs a script (with its own values)
or another workflow. Workflows live in any of the three trees, next to
scripts.

**Link.** A named URL, local file or folder that opens with its default app.

**Schedule.** A trigger attached to a script or workflow: a time, an interval,
or an event (a file changing, BatchPad starting, another run finishing).

**Run.** One execution of a resolved script: the command line, the environment,
a start time, streamed output, the exit code and the duration.

### Three trees, one model

```
▾ ★ My Scripts                    (per user, per workspace, editable here)
    ▸ Daily
      Build Release · SpaceTrader     ← customises Workspace › Build › Build
      Serve web :9000                    ← customises Workspace › Web › Serve
▾ 📁 Workspace — My Project        (batchpad.json, shared via git)
    ▸ Build
    ▸ Test
    ▸ Web
▾ 🌐 Global                        (global.json + imported libraries)
      Clean temp folders
```

All three trees hold the same node types (folder, script, workflow and link), so the tree
view, the runner and the parameter form work identically everywhere. All
three trees are edited in the app (§5.1). Editing a shared tree is marked
clearly, because the change reaches everyone who uses that repository.

## 3. File formats

The app writes these files, and users are not expected to open them. They
are still plain JSON so they diff and merge well in git and can be
hand-edited when someone prefers to:

- **Deterministic output.** Stable key order, two-space indent, one node per
  block, and only non-default values stored, so a UI change produces a small,
  readable diff.
- **Tolerant input.** Comments and trailing commas are accepted when reading.
  The app does not preserve comments when it rewrites a file, so a
  `description` field takes the place of comments.
- Unknown properties are preserved, so newer configs survive an older BatchPad.
- Writes are atomic (temp file, then rename) and re-read the file first, so
  two BatchPad windows editing `user.json` do not lose each other's changes.
- If the file changes on disk while an edit is open (a `git pull`), the app
  reloads and re-applies the pending edit, or asks when both touched the same
  entry. The workflow editor and the settings pages still ask on any change.

The examples below show the storage format that the UI in §5.1 edits.

### 3.1 Script tree node

A node with `items` is a folder, a node with `steps` is a workflow (§4.1), a
node with `url` is a link, and any other node is a script.

```jsonc
{ "folder": "Build", "items": [ /* nodes */ ], "collapsed": false }
{ "name": "QA report", "url": "qa_report.html" }                 // local file, relative to the config
{ "name": "Local web build", "url": "http://localhost:8123/" }
{ "name": "Profiles", "url": "build/profiles/", "icon": "folder" }  // a folder opens in Explorer
```

**Links** open web pages, report files and folders with their default app.
A link to a local file shows its age (*"updated 2 h ago"*) and greys out when
the file is missing. Links can use variables (`${workspaceDir}`,
`${env:X}`). They live in any tree, so a project can share its dashboards and
report files, and a user can keep their own. Make one by dragging a file or
folder from Explorer, or a URL from the browser, into My Scripts. A result
file on a run tab (`artifacts`) has "Pin as link".

### 3.2 Script definition

**References and ids.** Every reference (`base`, `run`, `target`, `stop`,
`dependsOn`, `afterRun`) uses the form `<tree>:<id>`, where the tree is
`workspace`, `global` or `global:<library>`. A bare id means "in the same
tree". Ids must be unique within a tree after `include`s are merged; a
duplicate is a load error that names both files. A discovered script has no
id until something references it. At that point the app assigns one from
the file name (`tools/regen_assets.py` → `regen-assets`, with a suffix if
taken) and stores it in the script's entry.

```jsonc
{
  "id": "build",                  // stable id, unique in its tree (see References)
  "name": "Build",
  "description": "Builds game exes and test projects.",
  "path": "build.bat",            // or "command": "git fetch --all", or "module": "pytest" (python -m)
  "runner": "auto",               // auto | batch | python | csharp | powershell | exe | shell
  "workingDir": "${workspaceDir}",
  "args": ["--no-logo"],          // fixed leading arguments, always passed
  "params": [ /* §3.3 */ ],
  "env": { "PYTHONUNBUFFERED": "1" },
  "envFile": ".env",              // optional dotenv file
  "console": "captured",          // captured | window | windowKeepOpen
  "longRunning": false,           // server/watcher: running badge, stop button, no timeout
  "ready": { "pattern": "Serving .* on (http://\\S+)", "open": "$1" },
  "stop": "stop-web",             // id of a script that stops this one (else kill tree)
  "lock": "native-build",         // runs sharing a lock name queue instead of overlapping
  "lockScope": "checkout",        // checkout | machine: lock and singleInstance hold per git checkout, or across all (§4)
  "confirm": "Deploys to the public site. Continue?",
  "errorPatterns": [": error ", "LNK\\d{4}"],   // highlighting only, never decides success
  "artifacts": [{ "path": "qa_report.html", "open": "onSuccess" }],  // links on the run tab; open: never|onSuccess|always
  "dependsOn": ["generate"],      // run these first, stop on the first failure
  "testReport": "build/qa/junit.xml",   // JUnit XML read after the run (§5); or
                                        // { "path": …, "rerunParam": "match", "rerunBy": "case"|"suite" }
  "singleInstance": false,        // true: a second run waits instead of starting
  "timeout": "30m",               // kill the tree after this long; none for longRunning
  "elevated": false,              // run as administrator; forces window mode (output cannot be captured)
  "nameTemplate": "Build ${param:config.label} & run ${param:app}",  // names new customisations (§3.7)
  "hidden": false,                // hides a discovered script (§3.10)
  "tags": ["web"],
  "icon": "globe",
  "hotkey": "Ctrl+Shift+B"
}
```

`runner: auto` picks by extension: `.bat`/`.cmd` → batch, `.py` → python,
`.cs` → csharp, `.ps1` → powershell, `.exe` → exe.

### 3.3 Parameters

```jsonc
{ "name": "mode", "label": "Mode", "type": "choice", "choices": ["fast", "full"], "default": "fast",
  "description": "Shown as help under the field", "required": true }
{ "name": "verbose", "type": "flag", "arg": "--verbose" }
{ "name": "tier", "type": "choice", "arg": "--benchmark_filter=", "choices": ["^Smoke", "^Deep"] }  // arg ending in "=" joins: --benchmark_filter=^Smoke
{ "name": "port", "type": "int", "arg": "--port", "default": 8123, "min": 1, "max": 65535 }
{ "name": "targets", "type": "text", "split": true }     // "A B" → two arguments
{ "name": "app", "type": "choice", "choicesFrom": { "glob": "build/web/*.html", "stem": true } }
{ "name": "suite", "type": "choice", "choicesFrom": { "command": "tools/list_suites.bat" } }
{ "name": "out", "type": "path", "mode": "folder" }
{ "name": "token", "type": "secret" }                     // prompted each run, never saved
```

Types: `flag`, `choice`, `multichoice`, `text`, `int`, `path`, `secret`.

**Rich choices.** A choice can be an object that carries extra fields, so one
picked value can drive several places in the command:

```jsonc
{ "name": "config", "type": "choice", "default": "Release", "choices": [
    { "value": "",          "label": "Debug",   "dir": "debug" },
    { "value": "--release", "label": "Release", "dir": "release" },
    { "value": "--final",   "label": "Retail",  "dir": "retail" } ] }
```

`${param:config}` is the value (`--release`), `${param:config.label}` is
`Release`, and `${param:config.dir}` is `release`.

**Stored values are values, not labels.** `default`, My Scripts `values`,
step values and schedule values all store the choice's `value`. For
convenience when reading, a string that matches no value but matches exactly
one label is accepted and rewritten to the value on the next save. The UI
always shows labels.

**Discovered choices** (`choicesFrom`) keep the project as the single source
of truth instead of copying lists into the config:

| Source | Example |
|---|---|
| `glob` | `{ "glob": "build/web/*.html", "stem": true }` |
| `glob` + capture | `{ "glob": "src/*/qa/use_cases.md", "match": "src/([^/]+)/" }` gives the folder names |
| `regex` over a file | `{ "file": "build.bat", "regex": "set \"GAMES=([^\"]*)\"", "split": " " }` |
| `regex`, all matches | `{ "file": "CMakeLists.txt", "regex": "add_game_app\\((\\w+)", "all": true }` |
| `command` | `{ "command": "tools/list_suites.bat" }` (one choice per output line) |
| `list` | `{ "list": "games" }`, a named list in the file's `"lists": {}` |

Choices are refreshed when the source file changes. A `command` source runs
only in a trusted workspace and must finish within 10 s; its output is kept
until the script file changes or the user presses **Refresh choices**. A
`command` that is not a file runs as a shell command. Several sources can be
combined in an array, and duplicates are removed. Any source takes:
- `"valueTransform": "lower"`: keeps the label as found and passes the value
  lowercased, e.g. `RobotManager` → `robotmanager`;
- `"relativeTo": "<dir>"`: makes glob results relative to a folder, usually
  the script's working directory.

A rich choice with `"split": true` passes its value as several arguments
(`--release --asan`).

**Shared parameters.** A workspace defines a parameter once, under
`"sharedParams": { "app": { … }, "config": { … } }`. Scripts then refer to it
with `{ "use": "app" }`, overriding fields if needed:
`{ "use": "app", "arg": "--target" }`. Every script that builds, runs or
packages an app then shows the same picker.

**Parameters that emit nothing.** `"emit": false` keeps a parameter out of
the command. It is only for `${param:…}` use elsewhere: a `ready.open` URL,
an artifact path, a workflow value.

**Parameters as environment.** `"envVar": "GAME_AUDIO_DRIVER"` sets an
environment variable from the value instead of adding an argument. Some
programs take options only that way.

**Multichoice (pick a set).** It is shown as a filterable checklist with
All/None buttons. The value is a list, emitted as separate arguments (or
once per item with `"repeatArg": true`, e.g. `--filter A --filter B`).
`"emptyMeans": "all"` labels an empty selection "All" and emits nothing (or
`"emptyArgs": ["--tests"]` instead), which suits scripts that run everything
when given no names. `"maxPerCall": 9` splits a larger selection into several
consecutive runs of the script. This is for scripts that read only
`%~1`…`%~9`, which would otherwise silently drop the rest. A checked set is
saved by saving the script to My Scripts under a name (*"Physics tests"*).

**Ask on run.** `"ask": true` on a parameter (or on a My Scripts entry for a
chosen parameter) shows a small picker when the script runs, pre-selected
with the last value used. It is the alternative to saving one favourite per
app. Such a parameter stays out of the details form. A `secret` parameter is
a password box in the form, is asked for at run time when left empty, and is
never saved to `user.json` or history.

**Argument assembly.** Fixed `args` come first, then each parameter in declared
order:

- `flag`: emits `arg` when on.
- Value types: when the value is non-empty, emit `arg` (if set) and then the value.
  An `arg` ending in `=` is joined to the value as one argument
  (`--benchmark_filter=^Smoke`).
  With `split: true` the value is split on whitespace (quotes respected).
- A parameter with `"position": "end"` is emitted after the others (for trailing
  positional lists).

For scripts that need a different order, `argsTemplate` overrides assembly:
`["{config}", "--port", "{port}", "{targets...}"]`, placed after the fixed
`args`. `{name}` expands to the value (a flag to its `arg` when on), `{name...}`
to one argument per list item, and an element that expands to nothing is
dropped. `envVar` parameters still go to the environment.

### 3.4 Variables

Usable in every templated string: `path`, `workingDir`, `args`, parameter
defaults, `env` values, `ready.open`, `artifacts`, `testReport`, link `url`s,
step values and `nameTemplate`.

| Variable | Value |
|---|---|
| `${workspaceDir}` | directory of `batchpad.json` |
| `${scriptDir}` | directory of the script file |
| `${env:NAME}` | process environment |
| `${param:name}` | a parameter's current value (lets `ready.open` use the port) |
| `${param:name.field}` | `label` or an extra field of a rich choice (§3.3) |
| `${name}` | entries of the file's top-level `"variables": {}` |
| `${item}`, `${item.field}` | the current item inside a `forEach` step (§4.1) |
| `${workflow.name}`, `${workflow.result}` | inside a workflow |
| `${steps.<id>.exitCode}`, `${steps.<id>.<output>}` | an earlier step's result (§4.1) |

Filters: `${param:app|lower}`, `|upper`, `|quote`. Case matters because exe
names are often a lowercased project name.

Resolution order: parameter, then file variables, then built-ins, then
environment. An unknown variable is an error shown before running, never an
empty string.

### 3.5 Workspace file — `batchpad.json`

```jsonc
{
  "$schema": "https://…/batchpad.schema.json",
  "id": "5c0f…",                 // written when BatchPad creates the file; keys My Scripts storage
  "name": "My Project",
  "variables": { "webDir": "${workspaceDir}/build/web" },
  "defaults": { "console": "captured" },   // defaults for every script in this file
  "env": { "PYTHONUNBUFFERED": "1" },       // applied to every run from this workspace (§4)
  "envFile": ".env",
  "include": ["tools/batchpad.tools.json"], // split big configs; merged as sub-trees
  "scriptFolders": [ /* §3.10 */ ],
  "scripts": [ /* nodes */ ]
}
```

BatchPad finds a workspace by, in order: the path given on the command line,
`batchpad.json` in the current directory or a parent, or the last-used
workspace. The recent list lives in settings.

**Schema.** [`docs/batchpad.schema.json`](batchpad.schema.json) describes this file, so
editors offer completion. BatchPad writes `$schema` into the workspace files it
creates. `global.json` and `user.json` can point at
`batchpad.schema.json#/$defs/globalFile` and `#/$defs/userFile`, which also allow
`schedules`. The workspace file rejects `schedules`.

**Includes.** Each `include` path is relative to the including file, and an
included file may include others. An included file shows as a sub-folder
named by its `name` (else its file name). Its scripts resolve paths relative
to their own file and are saved back to it. They share the workspace's
`variables`, `env`, `sharedParams` and `lists`, and its id space (a bare id
or `workspace:<id>` reaches them). A missing file, or one included twice, is
a load error.

### 3.6 Global library — `%APPDATA%\BatchPad\global.json`

It uses the same shape as a workspace file, plus `"libraries": ["\\\\server\\share\\team.json"]`.
Each imported library shows as a sub-folder of Global. Its scripts resolve
paths relative to their own file. A library is named after its file
(`team.json` → `global:team:<id>`). It has its own id space, so a bare id
inside it means the library itself.

### 3.7 My Scripts — `%APPDATA%\BatchPad\workspaces\<workspaceId>\user.json`

```jsonc
{
  "scripts": [
    { "folder": "Daily", "items": [
      {
        "base": "workspace:build",          // §3.2 References
        "name": "Build Release · SpaceTrader",
        "values": { "config": "--release", "apps": ["SpaceTrader"] },
        "extraArgs": "",
        "env": {}
      },
      {
        "base": "workspace:qa-sweep",       // a workflow customisation
        "name": "Nightly QA sweep",
        "values": { "game": [] },           // workflow parameters
        "stepValues": { "report": { "open": "never" } }   // per-step overrides, by step id
      },
      { "name": "My scratch script", "path": "D:/scratch/try.py" }   // standalone
    ]}
  ],
  "schedules": [ /* §4.2 */ ]
}
```

**Resolution of a customisation.** Start from the base definition, overlay any
script fields the customisation sets (name, env merged key-wise, console, and
so on), then apply `values` as the parameter values. When the base changes
upstream (a new parameter, a new path), the customisation picks it up
automatically. It stores only the differences.

**Many variants of one script.** Saving the same script several times with
different values is the normal case (*Build & run · RallyRacer*,
*Build & run · BikeTrials*). "Duplicate" and "Save as new My Script" in the
details panel do it in one click. A shared script can set
`"nameTemplate": "Build ${param:config.label} & run ${param:app}"`, which names
new customisations automatically from their values until you rename them.

**Broken references.** If the base id is missing, the entry stays in the tree,
shown greyed with a warning and its saved values kept, until the user deletes
it or re-points it with "Change base…".

**Workspace identity.** Stored per `id`, so moving or re-cloning a repo keeps
favourites. A file without `id` falls back to a hash of its full path, and
the app offers to add one.

### 3.8 App settings — `%APPDATA%\BatchPad\settings.json`

Recent workspaces, window layout, theme (System/Light/Dark), interpreter
overrides (`python`, `pwsh`, `dotnet`), default editor, history retention,
`telemetry` (§4.4) and `mcp.allowIds` (§4.5), edited on the Settings page.
The editor is `editorCommand`, a command line with `{file}`, `{line}` and
`{col}` (default `code -g "{file}:{line}"` when `code` is on PATH, otherwise
the file's default app, or Notepad for script and program files). The path
reaches the command through a variable, so `&`, `|`, `^` and `%` in it are
inert.
**Portable mode:** if `batchpad.portable` sits next to the exe, every
`%APPDATA%\BatchPad` path moves to a `data\` folder beside it, and every
`%LOCALAPPDATA%\BatchPad` path to `data\local\`. This supports
sharing BatchPad on a USB stick or inside a tools repo.

### 3.9 Run history — `%LOCALAPPDATA%\BatchPad\history\<workspaceId>\`

One JSON record per run (script, resolved command, values, trigger, exit code,
times, time queued for a lock, folder and tags, git branch and commit, test
counts, error lines, and for a workflow step the workflow run it belongs to)
plus a log file. Secret values are masked in the record, command and log.
Pruned by count and age (default 500 records, 30 days). Last-result badges are
loaded from it when a workspace opens. The History tab lists recent runs with
"Open log" and "Run again", which reuses the recorded values except secrets.
It is kept in LOCALAPPDATA because it is machine-specific and can grow large.
Runs recorded by another process, such as the command line, appear live.

### 3.10 Script folders, discovery and detection

**Script folders.** A workspace (and the global library) lists folders that
BatchPad watches. Every script in them appears in the tree without any
configuration. Adding a script means saving a file there.

```jsonc
"scriptFolders": [
  { "path": ".batchpad/scripts" },                       // the default; "New script…" creates files here
  { "path": ".", "include": ["*.bat"], "recurse": false },
  { "path": "tools", "include": ["*.py", "*.bat", "*.ps1"], "exclude": ["test_*", "*_lib.py"] }
]
```

- Sub-folders become tree folders. Scripts at a folder's top level can be
  grouped by filename prefix (`build_*`, `package_*`) when `"groupByPrefix":
  true`.
- A discovered script needs no entry in `scripts`. It gets one only when
  something about it is changed in the UI (name, parameters, placement, and
  so on). The entry is keyed by `path`, so it survives the file being edited.
- Hiding a discovered script stores `{ "path": "…", "hidden": true }`. Renaming
  or moving the file (or its folder) on disk carries its entry, seen state and
  dismissed proposals along when BatchPad sees the rename. Otherwise the entry
  shows as orphaned, with "Re-attach…", which points it at another file and
  keeps its settings.
- A new file appears live, marked **New** until it is opened once. The
  opened paths are kept in `user.json` as `seenPaths`; the first open of a
  workspace records every existing script as seen.

**Detection.** When a script is discovered, or its file changes, BatchPad
reads it without running it and proposes settings:

| Detected | From |
|---|---|
| Name | the filename made readable: `package_web.bat` → *Package web* |
| Description | the first comment block: `rem`/`::`, `#`, a Python docstring, PowerShell `.SYNOPSIS`, C# `//` or `///` |
| Parameters (Python) | `argparse` calls, read statically via Python's `ast` (flags, choices, `type`, defaults, `help`, `nargs`) |
| Parameters (PowerShell) | the `param()` block via the PowerShell parser (types, `ValidateSet`, defaults, `[switch]`) |
| Parameters (batch) | usage lines in the header (`rem   build.bat --release [name]`) and `%~1`…`%~9` use, as flags and positional text |
| Parameters (C#) | `args[n]` use, as positional text; `System.CommandLine` options later |
| Long-running | `http.server`, `serve` in the name, `Press Ctrl+C` in the text; never for a `stop_…`, `kill_…` or `shutdown_…` script |
| Stop companion | a `serve_X` / `stop_X` pair with a shared parameter |

The Python and PowerShell readers run a helper process (`py -3`, or the
PowerShell that runs scripts), so they run only for trusted folders, give up
after 10 s, and are cached until the file changes.

Detected settings are proposals. The editor shows them with a *detected*
badge and **Accept all / accept one / dismiss**. Until accepted, a
discovered script runs with just the detected name and no parameters, so
nothing guessed can change what runs. When a script changes and detection
finds something new (*"1 new option: --gated"*), a badge on the script offers it.
The badge shows whatever is proposed and not yet in the entry or dismissed;
clicking it opens the editor's proposals. Dismissals are kept per script in
`user.json` as `dismissedProposals`. The tree's Python and PowerShell badges
use a probe only once the file has changed (or its result is already cached),
so opening a workspace starts no process. A `serve_X`/`stop_X` pair that
shares a parameter proposes `stop` on the serve script, and accepting it gives
the stop script an entry with an id when it has none.

## 4. Execution

| Runner | Command |
|---|---|
| batch | `cmd.exe /d /v:off /s /c ""<script>" <args>"` |
| python | `py -3 -u <script> <args>` (or `-m <module>`), falling back to `python -u` when there is no launcher |
| csharp | `dotnet run --file <script> -- <args>` (.NET 10 file-based apps); `.csx` via `dotnet script` if installed |
| powershell | `pwsh -NoProfile -ExecutionPolicy Bypass -File <script> <args>`, falling back to `powershell.exe` |
| exe | the file itself |
| shell | `cmd.exe /d /v:off /s /c "<command>"` for inline `command` entries |

**Captured mode** (default): stdout and stderr are redirected and streamed
line by line into the output panel with stderr tinted. Stdin is redirected
and closed immediately, so `pause` and prompts return instead of hanging.
Output is decoded line by line. A line is read as UTF-8 when it is valid
UTF-8, and otherwise with the OEM code page. This matters because a batch
file mixes OEM output from cmd with UTF-8 from the Python it calls.
`PYTHONIOENCODING=utf-8` is set, and ANSI colour codes are rendered — the 16
colours and bold, each line starting unstyled; other escapes are stripped
(`FORCE_COLOR=1` is set for runners that honour it). A run keeps its last
20,000 lines for tabs opened later and for history, after a
"… N earlier lines not kept" note.

**Quoting.** Arguments are quoted per target, never by one generic rule. Exes
and Python get standard `CommandLineToArgvW` quoting. Anything that goes
through `cmd.exe` (batch, shell) also gets cmd escaping of `^ & | < > % !`
and quotes, so `^Smoke` reaches the script intact. (A batch file has no
escape for an embedded `"`, so one arrives doubled.) Both escapers are
unit-tested against real `cmd`. Paths longer than 260 characters work,
because the app manifest is `longPathAware`.

**Window mode:** a real console window for interactive scripts, or for
scripts whose output you want kept separately. BatchPad still tracks the
process and its exit code. `windowKeepOpen` uses `cmd /k` so the window
survives the script.

**Launching apps.** Running a built program is just an `exe` entry whose path
is templated, e.g. `build/${param:config.dir}/bin/${param:app|lower}.exe`.
The working directory defaults to the exe's folder, because that is where its
data sits. Such entries are `longRunning`: the tree shows them running, Stop
closes them, and their stdout/stderr (logs, asserts) are captured like any
other run. A missing exe fails before launch with "not built yet".

**Success is the exit code.** `errorPatterns` only highlight lines and let you
jump between errors. They never turn exit code 0 into a failure or the
reverse.

**Stopping.** Every run is started inside a Windows Job Object, so its whole
process tree is tracked, including children that detach with `start` or are
orphaned by a crash. Stop works in this order:
1. The script's `stop` companion, if it has one. It receives the same
   parameter values, so the port matches.
2. Otherwise, for GUI apps, a polite close (WM_CLOSE to the top-level
   windows).
3. After a grace period, termination of the job.

Closing BatchPad with runs in progress asks first: Yes stops them and then
closes, No leaves them running, Cancel keeps the window open. A long-running
run records its process id and start time in
`%LOCALAPPDATA%\BatchPad\running.json` once its process starts (one still
queued for a lock is not recorded), and forgets them when it ends. When the
workspace opens again, a recorded process that is still alive with the same
start time (so a reused process id is not taken for it) comes back as an
adopted run: its tab and badge show it running, without its output, and Stop
runs its stop companion or ends its process tree.

**Elevation.** `elevated: true` runs through a UAC prompt. Windows cannot
redirect an elevated process's output, so those runs always use window mode
and report only the exit code.

**Locks and concurrency.** A script may run several instances at once unless
`singleInstance` is set. Runs sharing a `lock` name wait in a visible queue.
Locks are re-entrant within one workflow run, so a workflow holding
`native-build` can run a step that takes `native-build` too. `singleInstance`
is not: two parallel steps running the same script still take turns, and it
is per workspace, so another workspace's script with the same id never waits. A queued run
shows "waiting for lock X" on its tab and its tree badge, and Stop takes it
out of the queue without starting it. Locks and `singleInstance` hold across
processes (the app, command-line runs and agents all share them; §4.5) and,
by default, **per checkout**: two git worktrees of one repository build into
separate folders, so their `native-build` locks don't block each other. For
things every checkout shares, such as a network port or a device, set
`"lockScope": "machine"` on the script or workflow.

**Unattended runs.** Scheduled runs and CLI runs have nobody to answer
prompts. `confirm`, `ask` and `secret` then fail the run immediately, with a
message saying which value is missing, unless the value was supplied: the
schedule's `values`, the CLI's `--set` and `--yes`. (Reading an unattended
run's secret from Windows Credential Manager is not built yet, so such a run
must be given the value.) Secret values are masked in command previews, logs and
history.

**Prerequisites.** `dependsOn` runs prerequisites first, in order, and stops
on the first non-zero exit code. A prerequisite's own `dependsOn` runs before
it, and each script runs once. The run shows as a workflow tab (`generate →
build`), with prerequisites receiving parameter values that share a name. A
workflow step does not expand its script's `dependsOn`; the workflow lists
its steps itself. Use it for "always generate before build". Anything more
is a workflow.

**Environment.** Starting from BatchPad's own environment, apply in order:
workspace `envFile`, workspace `env`, script `envFile`, script `env`, then
customisation `env`. Variable names are case-insensitive, as Windows treats
them, so `Path` and `PATH` merge into one.

### 4.1 Workflows

A workflow chains scripts into a pipeline, for example *generate → build Release →
run tests → package web → deploy*. It is a node like a script: it sits in any
tree, runs from the tree, and can be dragged into My Scripts. Building a
workflow does not need JSON. You drag scripts from the tree into the workflow
editor and set each step's values there.

```jsonc
{
  "id": "nightly-web",
  "name": "Nightly web release",
  "params": [ { "name": "app", "type": "choice", "choicesFrom": { "glob": "build/web/*.html", "stem": true } } ],
  "steps": [
    { "id": "gen",   "run": "workspace:generate" },
    { "id": "build", "run": "workspace:build", "values": { "config": "--release", "targets": "${param:app}" } },
    { "parallel": [
        { "id": "tests", "run": "workspace:tests", "values": { "match": "${param:app}" } },
        { "id": "web",   "run": "workspace:package-web", "values": { "app": "${param:app}" } }
    ]},
    { "id": "deploy", "run": "workspace:deploy", "when": "success", "confirm": true },
    { "id": "notify", "run": "global:toast", "when": "always",
      "values": { "text": "${workflow.name}: ${workflow.result}" } }
  ],
  "lock": "native-build"
}
```

Step semantics:

- Every step has an `id`. The editor assigns one, and it is used for
  `${steps.<id>.…}` and for `stepValues` in customisations (§3.7).
- A step's `values` are optional for parameters the workflow shares by name.
  A workflow parameter `app` flows into every step that has a parameter
  `app` (or `use`s the shared one), including through nested workflows. The
  common case needs no wiring. An explicit step value always wins.
- **Typed values.** A value that is exactly one `${param:x}` or `${item}`
  passes the typed value: a multichoice list stays a list, and an empty
  list stays empty, so the step's own `emptyMeans` applies. A value with
  other text around it becomes a string, and lists are joined with spaces.
- A step may set `emptyArgs` to override what its target's multichoice emits
  when the passed list is empty (Build with no apps means `--tests` in one
  workflow and `--bench` in another).
- `forEach` runs a step once per item of a list, usually a multichoice
  parameter:
  `{ "id": "benches", "forEach": "${param:benches}", "run": "workspace:run-bench", "values": { "bench": "${item}" } }`.
  If the list is empty and its parameter has `emptyMeans: all`, the loop runs
  over **every** choice. Items run one after another by default
  (`"parallel": 4` to fan out). The output tab shows a pass/fail row per
  item. One failing item does not stop the others (`"failFast": true`
  changes that), but it does fail the step, and so the workflow, unless
  `continueOnError` is set.
- **Long-running last steps.** A workflow whose last step is long-running
  (*Build & run*, *Build web & serve*) is *done* when that step becomes
  ready. That step has a `ready` pattern, or has started, for launched apps.
  At that point the result badge shows success, workflow locks are
  released, and the step keeps running as its own run with its own Stop.
- Steps run in order. A `parallel` group runs its members at once and
  finishes when all of them have finished.
- `when`: `success` (the default: every earlier step passed), `failure`
  (something failed, for cleanup and alerts), or `always`.
- `continueOnError: true` lets a step fail without failing the workflow.
- Values reach steps through workflow parameters (`${param:x}`) and through
  earlier steps' outputs: `${steps.build.exitCode}`, plus any value a step
  exports by printing a `::set name=value` line. Those lines are left out of
  the step's log, and the values show on its row.
- `retry: { count, delaySeconds }` for flaky steps (network deploys). Each
  attempt is logged in the step's output.
- Steps may reference another workflow. Cycles are an error at load time.
- A workflow run shows as one output tab with a step list on the left:
  status, duration and exit code per step, each with its own log (with the
  run tab's search, F8 and source links). Members of a parallel group and a
  nested workflow's steps are indented under their row, which also shows the
  step's outputs and retry attempts. A step's
  `artifacts` show as links on its row and open after it per `open`, as in a
  lone run. You can
  cancel the whole workflow, which stops every running step. A failed
  workflow can be **re-run from the failed step** with the same values. The
  steps before it are not run again: their results and outputs are reused.
  A failed member of a parallel group re-runs the whole group, and a failed
  `forEach` item the whole step. History records the run as a resumption.
- A `lock` on the workflow applies to the whole run, and the workflow then
  takes its steps' locks up front too, so two workflows can't deadlock over
  each other's locks; otherwise each step's own lock applies.

The editor is a vertical list of step cards: drag to reorder, drop onto the
middle of a card to make a parallel group (a group card can be ungrouped), and
edit values in the same generated form scripts
use. A card also names its step `id`, its `emptyArgs`, and any value given as
a template (`cfg ← ${param:config.testFlag}`); a single value given to a
multichoice is stored as a one-item list. The JSON is the storage format. Hand-editing it is supported but not
required.

### 4.2 Schedules and triggers

A schedule attaches a trigger to a script or workflow, with fixed values.
Schedules are personal. They live in `user.json` (or in `global.json` for
workspace-independent jobs), never in the shared workspace file, so cloning a
repo never starts jobs on your machine. A schedule in `global.json` that
targets `workspace:…` must name the workspace (`"workspace": "C:/src/game"`).

A schedule records a hash of the definition it was set up with: the
scheduled script or workflow, every script and workflow it reaches, those
scripts' file contents, and the workspace's `variables`, `sharedParams` and
`lists`. If `git pull` changes any of them, the schedule pauses (checked on
reload and again before each fire). Editing a scheduled script yourself
pauses it too, until you re-confirm it. The
Schedules view then shows *"definition changed — review"* until the user
re-confirms it, so a change to a shared file cannot silently alter what
runs unattended.

```jsonc
"schedules": [
  { "id": "nightly", "target": "workspace:nightly-web", "values": { "app": "RallyRacer" },
    "trigger": { "cron": "0 2 * * 1-5" }, "missed": "runOnce", "enabled": true },
  { "target": "workspace:tests", "trigger": { "every": "30m", "between": "09:00-18:00" } },
  { "target": "workspace:regen-assets", "trigger": { "fileChanged": "assets/**/*.png", "debounce": "5s" } },
  { "target": "workspace:serve", "trigger": { "onStart": true } },
  { "target": "global:toast", "trigger": { "afterRun": "workspace:build", "result": "failure" } }
]
```

Triggers: `cron` (5-field), `every` (with an optional time window), `at` (one
shot), `fileChanged` (glob and debounce), `onStart` (BatchPad or workspace
opened), and `afterRun` (another run finished, filtered by result).
`afterRun` gives loose chaining without building a workflow.

Time triggers use local wall-clock time. `cron` takes lists, ranges, steps
and month and weekday names; when both the day and weekday fields are
restricted, either one matching is enough. `every` takes `90s`, `30m`,
`1h30m` or `2d` (at most 366 days) and counts from midnight, or from the start of its
`between` window (both ends included; a window may wrap past midnight).
`at` is a local date and time unless it carries an offset. A time skipped
when the clocks go forward fires when they jump; a time repeated when they go
back fires once.

**Where schedules run.**

1. **In-app (default).** The scheduler lives in BatchPad and fires while it is
   running, including when it is minimised to the tray. Every trigger type
   works, and runs appear in the normal UI and history. The `missed` policy
   (`skip` | `runOnce`) decides what happens to runs that fell due while the
   app was closed.
2. **Windows Task Scheduler (opt-in, time triggers only).** "Register with
   Windows" creates a task that calls `BatchPad.exe run <target> --workspace …
   --set …`. The task runs even when BatchPad is closed. Its runs still show
   in BatchPad's history, because the CLI writes history too.

Scheduled items get a clock badge in the tree. A **Schedules** view lists
every schedule with its next and last run, has an enable toggle and "Run now"
for each. When a schedule fires while its previous run is still going, the
new run is skipped by default (`overlap: skip | queue | parallel`). A failed
scheduled run raises a tray notification naming the schedule, so failures
cannot pass unnoticed just because nobody was watching; clicking it opens the
run's history entry. While any schedule is enabled, closing the window keeps
BatchPad running in the tray (a setting on the Schedules page, on by
default); the tray menu offers Open, Schedules and Exit, and Exit asks about
active runs as closing does.

A schedule written by hand without `definitionHash` adopts the definition it
is first loaded with, unless BatchPad's saved schedule state could not be
read: it then waits for a confirm. Only a definition the user confirmed in the Schedules
view answers a `confirm` question; otherwise such a target fails unattended.
`queue` holds at most one waiting fire. `runOnce` catches up one fire at
start, counting from the last fire, or from when the schedule was first
seen.

`fileChanged` globs are relative to the workspace folder, and a rename
matches by its old or new name; `debounce` defaults to 1s. `onStart` fires
once per session, when the schedule is first active. `afterRun` defaults to
`result: success`, and a stopped run never triggers it. A schedule that
`afterRun` would fire a second time in one chain is refused with a failure
naming the chain, so A → B → A stops after one round.

### 4.3 Trust

A `batchpad.json` arrives with a `git clone`, so it is someone else's code.
As in VS Code's workspace trust, each workspace folder is **untrusted** until
the user trusts it. The New workspace wizard asks, and so does opening a
workspace for the first time. The decision is stored per folder in settings.
A script from a file the workspace includes from outside its folder runs only
once that file's folder is trusted too.

An untrusted workspace is read-only in effect. The tree shows, the editor
works, and static detection (which reads files and runs nothing) works. It
does **not**:
- run anything, including `choicesFrom.command`, which would otherwise run on
  load;
- run `argparse` or PowerShell detection, which calls the interpreter;
- open links to executable file types (`.bat`, `.cmd`, `.exe`, `.lnk`, `.ps1`,
  and so on);
- register hotkeys from shared definitions;
- read outside the workspace folder through `include`, `scriptFolders` or
  `choicesFrom` `file` and `glob`; such paths are skipped with a load problem.

Even in trusted workspaces:
- links only open executable file types after a confirmation;
- a `ready.open` URL or an artifact opens by itself only when it is not an
  executable type and not on a network or device path; otherwise it stays a
  button;
- interpreters are resolved to full paths from settings or the `py`
  launcher, never from the repository's own folder;
- shared definitions cannot create schedules (§4.2);
- the workspace's files never reach device paths, or network shares outside
  the workspace folder. `global.json` and `user.json` are yours, so they may.

### 4.4 Run telemetry

Every run already leaves a history record (§3.9). Telemetry turns those
records into **events** that can be analysed locally and forwarded to a
monitoring backend. The goal is to answer questions like "how long do our
builds take, and is it getting worse?" and "how much of today went into
tests?".

**The run event.** One event per finished run, workflow and workflow step:

```jsonc
{
  "schema": 1, "eventId": "…", "runId": "…", "parentRunId": null,   // steps point at their workflow run (and carry stepId)
  "time": "2026-09-27T09:12:03.412Z", "queuedMs": 850, "durationMs": 94210,
  "workspace": { "id": "5c0f…", "name": "My Project", "branch": "main", "commit": "3f9a1c2" },
  "script": { "tree": "workspace", "id": "build", "name": "Build", "kind": "script", "folder": "Build", "tags": ["native"] },
  "trigger": "agent:claude-code",       // manual | cli | schedule:<key> | afterRun:<key> | resume:<step> | agent:<name>
  "outcome": "exited", "exitCode": 0,
  "tests": { "passed": 1284, "failed": 2, "skipped": 3 },           // when the run had a test report
  "machine": "WS-042", "user": "alex", "batchpadVersion": "0.2.0",
  "values": { "config": "--release" }                              // only with includeValues; secrets never
}
```

`queuedMs` is the time spent waiting for a lock. It is kept apart from
`durationMs`, so a blocked build doesn't look slow. The git branch and commit
come from the workspace folder when it is a git checkout, read from
`.git/HEAD` without running git. A retried step's attempts are not told
apart: the history record doesn't keep the attempt number.

**Insights (local, no backend).** An Insights view reads the run history and
shows:
- per script: runs, median and p95 duration, the trend over the last N
  runs, and the failure rate;
- per folder or tag: total time and share of time for a period, e.g.
  "Test: 38% of today's run time";
- time by trigger (you, agents, schedules), and repeats (the same script
  and values run many times in a row);
- the slowest tests from test reports, and tests that pass and fail
  alternately (flaky).

The command line has the same data as `batchpad stats [--since 7d] [--json]`.

**Forwarding (sinks).** Sinks are configured in `settings.json` only,
never in a workspace file, so a cloned repo can't send data anywhere. Every
sink is off until you add it:

```jsonc
"telemetry": {
  "machine": true, "user": false,           // include the machine and user names
  "includeValues": false,                   // parameter values (secrets are never sent)
  "hashNames": false,                       // hash the names in each event (below)
  "sinks": [
    { "type": "jsonl", "path": "%LOCALAPPDATA%\\BatchPad\\telemetry\\runs.jsonl", "maxSizeMb": 50 },
    { "type": "otlp", "endpoint": "http://localhost:4318", "headers": { "Authorization": "${env:OTEL_TOKEN}" } },
    { "type": "elastic", "url": "https://es.example:9200", "index": "batchpad-runs", "apiKey": "${env:ES_API_KEY}" },
    { "type": "influx", "url": "http://influx:8086", "org": "dev", "bucket": "batchpad", "token": "${env:INFLUX_TOKEN}" }
  ]
}
```

| Sink | Format | Notes |
|---|---|---|
| `jsonl` | One event per line, appended to a file, rotated by size | Works with any backend today, through Filebeat, Vector, Fluent Bit or Telegraf |
| `otlp` | OpenTelemetry OTLP/HTTP (JSON): a run is a span, and a workflow is a trace with its steps as child spans | The vendor-neutral standard: an OTel Collector forwards to Elastic, Influx, Grafana, Jaeger and others |
| `elastic` | Elasticsearch `_bulk` NDJSON | Direct, no collector |
| `influx` | InfluxDB v2 line protocol: measurement `batchpad_run`, tags script/folder/trigger/outcome, fields duration/queued/exit code | Direct, no collector |
| `http` | A JSON array of events, POSTed | Generic webhook |

Credentials come from `${env:…}` (or later from Windows Credential Manager),
never written in plain text by the app. An unset variable fails the send as
retryable, as do 401 and 403, so a missing or expired key loses nothing;
other 4xx answers drop the batch. Redirects are not followed, so headers
never reach another host.

`hashNames` replaces user-chosen names with a salted hash (HMAC-SHA256 with a
per-install salt in `telemetry\salt`): workspace, script, folder and checkout
names, script id, branch, step id and tags, and the schedule or step a
trigger names, keeping its kind (`schedule:<hash>`, `afterRun:<hash>`,
`resume:<hash>`). Agent triggers (`agent:claude-code`) and plain ones (`cli`,
`manual`) are sent as is.

**Delivery never slows a run.** Events go to a local outbox
(`LocalDirectory\telemetry\outbox\`, one file per batch). A background sender
posts batches with exponential backoff and drops the oldest when the outbox
passes its cap (default 20 MB). Every sink shows its state on the Settings
page (toolbar): last success, last error, and how many events are waiting.
**Send test event** checks a configuration. The command line and the scheduler write events the
same way; a sender idle for a minute rechecks the outbox, so what a process
could not deliver before exiting goes out from any other that is running.
Each process enqueues only the runs it recorded itself (the history store
tells its own saves apart from records another process wrote), so a
command-line run the app also shows is sent once.

### 4.5 Coding agents

Coding agents such as Claude Code build and test through shell commands.
Running those through BatchPad gives them the project's own named, correct
command lines, shares locks with people and other agents, and records how
long everything takes (§4.4). That makes it visible when an agent spends
most of its time in a slow test suite, or runs the same build over and
over.

**An agent-friendly command line.**
- `batchpad list --json` lists every runnable id with its name, folder,
  description and parameters (types, choices, defaults), so an agent can
  find what to run without reading the config.
- `batchpad run <id> --set name=value … --json` streams nothing and prints
  one result object at the end: exit code, outcome, duration, queued time,
  log path, the test summary with failed test names, and the error lines
  with their `file(line)` locations.
- `--errors-only` prints only error-pattern and stderr lines and the summary.
  An agent gets what it needs to fix a build without a 5,000-line log, which
  saves most of the tokens a build costs. The full log stays in history, at
  the printed path.
- `batchpad log <run-id> [--tail N] [--errors]` reads a recorded run's log
  later.
- `batchpad stats --json` gives the Insights figures (§4.4) to an agent or a
  report.

**Attribution.** `--agent <name>` records the run with trigger
`agent:<name>`. Without the flag, a run started from a coding agent's
environment is detected where possible (Claude Code sets `CLAUDECODE=1` in
the shell it runs commands in) and recorded as `agent:claude-code`.

**Locks across processes.** Agents often run several `batchpad run`
commands at once, next to a person using the app. So named locks and
`singleInstance` are machine-wide:
- Each lock is a file, `locks\<hash>.lock` in the local data folder, opened
  for exclusive use for the run. Windows closes it when the process dies, so
  a crashed run never leaves a lock behind. A `<hash>.owner` file next to it
  names the holder. The process's in-memory queue keeps FIFO order within
  the process, and other processes poll for the file. A released file is
  closed even when a run in the same process is next; that run then takes
  it again like any other waiter.
- A queued command-line run prints "waiting for lock native-build (held by
  <script> since 09:12)" to stderr, so an agent sees why it is waiting.
- `--no-wait` fails fast instead of queueing. It applies to a single script;
  with a workflow or prerequisites it is a usage error.

**An MCP server.** `batchpad mcp` runs an MCP server over stdio with these
tools:

| Tool | Does |
|---|---|
| `list_scripts` | the runnable entries with their parameters (only the `mcp.allowIds` ones when that is set) |
| `run_script` | runs one entry and returns the same result object as `run --json` (errors only, plus the log path; `errorsOnly: false` adds the log's last 2000 lines); `noWait` fails at once when a lock is held |
| `get_log` | a recorded run's log by run id, from the directory's workspace only: tail, errors only, or a line range (ranges are MCP-only; `batchpad log` has tail and errors) |
| `get_stats` | the Insights figures for a period |

Every tool takes a **required `directory`**: the agent's own working folder,
from which the workspace is found as the command line finds it. It must be a
local drive path; a UNC or device path is refused before anything touches it. The server has no default workspace, because one MCP
server serves a whole agent session, including subagents that work in other
git worktrees. A default taken from where the server was started would
quietly run the main checkout's scripts for a worktree agent.

A cancelled tool call stops its run, recorded as stopped. When the client
closes the server, runs still in flight are stopped and recorded before it
exits. The server shares one telemetry pipeline, delivering in the
background, instead of flushing after every run.

Register it with `claude mcp add batchpad -- batchpad.com mcp`. Agents then
get typed tools, and the permission system can allow `run_script` for chosen
ids only (`mcp.allowIds` in settings.json). Allowing an id allows everything
that entry runs, including its prerequisites and workflow steps.

**Git worktrees.** Agents often work in worktrees (`git worktree add`), one
checkout per task. BatchPad makes sure a script always runs in the checkout
the agent is working in:
- **Resolution.** The command line resolves the workspace from its current
  folder, and the MCP tools from `directory`. Either way the worktree's own
  `batchpad.json` is found first; the file is committed, so every worktree
  has it. The search never goes above a worktree's root: a worktree without
  its own file (not yet committed) reports that no workspace was found
  rather than using the main checkout's around it.
- **Every result says where it ran.** Run and list results carry the
  checkout of the workspace used, `checkout: { directory, kind: "main" |
  "worktree", name, branch, commit, repository }`. The command line also prints "in <checkout>" on stderr in
  text mode, so a run in the wrong checkout is visible at once.
- **Trust follows the repository.** A worktree of a trusted repository is
  trusted. BatchPad reads git's own files in both directions: the worktree's
  `.git` file points at `<repo>/.git/worktrees/<name>`, whose `gitdir` file
  must point back at that worktree, and whose `commondir` leads to the
  repository. A hand-made `.git` file that points at a trusted repository is
  not enough. Paths in these files are followed only on the same drive, never
  to a network or device path, so checking an untrusted folder cannot reach
  out to a server. A deleted worktree that git has not pruned still verifies
  until `git worktree prune`; recreating a folder at that exact path needs
  write access there, which is accepted.
- **Locks are per checkout** by default (§4 Locks), so agents in separate
  worktrees build in parallel, and `lockScope: machine` covers shared ports
  and devices.
- **One identity, one history.** Worktrees share the workspace id, so My
  Scripts and the run history are shared. Each run records its checkout,
  and Insights and `batchpad stats` can filter or group by checkout.

**Trust stays with people.** The command line and the MCP server run only in
workspaces trusted in the app (§4.3). A future command-line trust command
would require an interactive confirmation, so an agent cannot trust a
workspace for itself.

**A project snippet.** The README gives a short CLAUDE.md section to paste
into a project: "build and test through `batchpad run <id> --errors-only`
from your working folder (or the MCP tools with `directory` set to it); see
`batchpad list --json`; check that the reported checkout is yours".

## 5. UI

WPF with the built-in Windows 11 Fluent theme. It follows the system
light/dark mode and uses the accent colour and Mica backdrop.

```
┌───────────────────────────────────────────────────────────────────────┐
│ [My Project ▾]  ＋ Add  ✎ Edit (F4)  ⚙ Workspace     🔍 Search (Ctrl+K) │
├──────────────────────┬────────────────────────────────────────────────┤
│ ▾ ★ My Scripts       │ Build Release · SpaceTrader      ✅ 0 · 42s │
│   ● Serve web :9000  │ Customises: Workspace › Build › Build          │
│     Build Release…   │ Builds game exes and test projects.            │
│ ▾ 📁 Workspace       │                                                │
│   ▸ Build            │ Config   [Release     ▾]                       │
│   ▸ Test         ✅  │ Targets  [SpaceTrader        ]              │
│   ▾ Web              │ Extra    [                      ]              │
│     ● Serve web      │ > cmd /d /s /c ""D:\…\build.bat" --release …"  │
│       Stop web       │                                                │
│ ▸ 🌐 Global          │ [▶ Run] [▶ Run in window] [■ Stop] [💾 Save]   │
│                      ├────────────────────────────────────────────────┤
│                      │ Build ✅ │ Serve web ● │ Tests ❌ │ History     │
│                      │ === Building SpaceTrader ===                │
│                      │ …                                              │
└──────────────────────┴────────────────────────────────────────────────┘
```

- **Tree:** three roots; badges for running (●), last result (✅/❌) and
  broken references. Filter-as-you-type. Double-click runs with the current
  values; Enter runs; F2 renames; Delete removes a My Scripts entry.
- **Drag and drop:** from a shared tree to My Scripts creates a customisation
  with the current values. Within My Scripts it moves and reorders. Dropping
  a script file from Explorer onto My Scripts creates a standalone entry.
  "Add to My Scripts" in the context menu does the same without dragging.
- **Details panel:** a form generated from the parameters, a live command
  preview (copyable), and a note of what the entry customises. Edits to a
  shared script's values are session-only until "Save as My Script".
- **Parameter form:** a choice with up to four options renders as a segmented
  toggle (*Debug | Release | Retail*, *Headless | Visible*); longer ones render
  as a searchable drop-down; a multichoice renders as a checklist.
- **Test results:** when a script writes JUnit XML (`"testReport":
  "build/qa/junit.xml"`; pytest `--junitxml` and gtest `--gtest_output=xml`
  both do), the run tab gains a Tests view: pass/fail/skip counts, a tree of
  suites and tests with times and failure messages, and a failures filter.
  The report is only read when it was written during the run. "Re-run failed"
  appears only when `testReport` is the object form with a `rerunParam`: it
  runs the script again with that parameter set to the failed test names
  (`rerunBy: "case"`, the default) or suite names (`"suite"`) — a list for a
  multichoice, else space-separated — and the other values unchanged.
- **Workflow editor:** Edit (F4) on a workflow opens it in place of the details (§4.1); the run form keeps the workflow's parameters.
- **Schedules view:** a page opened from the toolbar (or the command palette)
  in place of the details panel (§4.2). Each schedule shows its target, its
  trigger in words ("Weekdays at 02:00"), next and last run with the result,
  an enable toggle, Run now, Edit, and Re-confirm when it is paused for review.
  Add and Edit pick the target from the trees, edit the trigger by kind, take
  values through the generated form, and save to `user.json`, or to
  `global.json` for a global schedule. Saving from the editor confirms the
  target's current definition; a new schedule gets an id from its target's
  name, so editing its trigger keeps its state.
- **Output panel:** one tab per run with status, duration and exit code. It
  auto-scrolls and stops when you scroll up. It has search (shows only the
  lines containing the text), clickable `file(line[,col])` /
  `file:line[:col]` references to existing files (relative ones against the
  run's working folder) that open in the editor (§3.8), next/previous error
  (F8 / Shift+F8, over lines matching `errorPatterns` and stderr lines), copy
  all and "open log". Detected URLs are clickable.
- **Context menu:** Run, Run in window, Stop, Add to My Scripts, Copy command
  line, Open terminal here, Edit script, Reveal in Explorer, Show history.
- **Command palette (Ctrl+K):** fuzzy search over every script, workflow and
  link in the three trees, shown with its tree and folder. Enter runs it (or
  opens the link), Shift+Enter selects it in the tree. Commands such as "New
  script" and "Workspace settings" are listed too. The tree filter keeps its
  own box.
  The keyboard should reach everything.
- **Windows integration:** taskbar progress while running; a toast when a run
  that took over 10 s finishes while the window is unfocused; jump-list entries
  for pinned My Scripts; optional tray icon so servers keep running with the
  window closed.
- **Live reload:** config files are watched and reloaded on change, keeping
  the selection and the running processes.

### 5.1 Adding and configuring in the app

Everything in §3 has a UI. No setting requires opening a JSON file.

**Adding things**

- **Drop a file** into a script folder (§3.10): it appears by itself.
- **Drag a file from Explorer** onto the Workspace or Global tree (v1). BatchPad
  asks whether to *reference it where it is* or *copy it into the script
  folder*. Onto My Scripts, a script becomes a standalone entry. A folder, a
  URL or any other file becomes a link.
- **New script…** (v1) creates a `.bat`, `.py`, `.cs` or `.ps1` from a small
  template in the script folder and opens it in the user's editor. Its entry
  updates live as the file is saved.
- **New link…**, **New folder**, **New workflow…** from the tree's context menu.
  Links also come from dragging a URL or file. **New entry…** adds a script
  entry by name and path, placed in that folder; the path may be a template
  (`build/${param:config.dir}/bin/${param:app|lower}.exe`) or empty (a Python
  `module`).
- **Add script folder…** in workspace settings, with include/exclude patterns
  and a live preview of what they match.
- **New workspace…** picks a project folder and scans it. It shows the scripts
  found as a checklist, grouped by folder, with detected details. It proposes
  script folders that cover them and writes `batchpad.json`. Adopting an
  existing repo takes one dialog.

**Script editor.** An *Edit* toggle on the details panel (or F4) turns the
run form into the definition editor, in tabs:

| Tab | Contents |
|---|---|
| General | name, icon, description, file, runner, working directory, console mode, long-running, confirm, module, lock |
| Parameters | list with add/remove/drag-reorder; per parameter: name, label, type, switch (`arg`), default, required, env var, ask on run, emit, split, `emptyMeans: all`, `emptyArgs`, `maxPerCall` (and lowercase values on a `use` entry). **Detected** proposals appear inline with Accept/Dismiss |
| Choices | for choice/multichoice: a grid of label, value, split and extra fields (`dir`, …), or **a source** (below) |
| Environment | key/value grid, env file picker |
| After run | ready pattern with a **tester** against the last run's output, open URL, stop companion (picked from the tree), artifacts, test report path, error patterns |
| Advanced | fixed args, argument template, name template, id |

A live command preview under the editor updates on every keystroke, and
**Test run** runs the edited definition before it is saved. Saving writes the
file (§3). Shared files show a banner: *"Shared script — saved to
batchpad.json in the repository. Everyone who pulls gets this change."*

**Choice-source picker.** Nobody should write a regex to list their apps. The
picker offers:

- *Files in a folder*: a folder and pattern chooser. Each name is taken from
  the file name or a path segment, which you click to select.
- *Lines in a file*: open the file and click a line. *"Every word after `=`
  on this line"* (as in `build.bat`'s `GAMES=` line) is one click. For other
  shapes, select an example value and BatchPad derives the pattern and
  highlights every match (v1). The regex is always shown, with a live
  preview, for people who want to edit it.
- *Output of a script* (v1): pick a script; one choice per output line.
- *Fixed list*: type or paste, with a label and a value per row. For
  example, the label `robots` could pass the value `robotmanager`.

Every source shows the resulting choices live, so a wrong pattern is obvious
before saving.

**Workspace settings** (a page, not a dialog): name, script folders, shared
parameters (the same parameter editor; each lists the scripts using it),
variables, environment, default runner options.

**Workflow editor** (§4.1): step cards, drag scripts in from the tree, per-step
values in the generated form, and parameter flow shown as chips
(*app → Build, Run app*).

**Promoting and demoting.** "Share with workspace" moves a My Scripts entry
into the workspace tree (after confirmation). A customisation becomes a real
entry with a new id: its values become parameter defaults and its extra
arguments fixed `args`. A standalone entry is copied as is. "Copy to My
Scripts" goes the other way: a standalone copy with absolute paths and
qualified references, so it runs the same command. A personal experiment can
become a team script without retyping it.

## 6. Feature research — prior art

| Tool | What it does well | Take for BatchPad |
|---|---|---|
| **VS Code tasks** (`tasks.json`) | `inputs` (pickString/promptString), `dependsOn`, problem matchers, `isBackground` with begin/end patterns, `${workspaceFolder}` variables | Parameter prompts, `dependsOn`, `errorPatterns`, `ready` pattern for servers, variable syntax |
| **JetBrains run configurations** | Shared ("store as project file") vs local configs; templates; compound configs; "before launch"; "allow parallel run" | Exactly the Workspace vs My Scripts split; `singleInstance`; sequences |
| **Taskfile / just** | Readable task files, descriptions, deps, `.env` loading, per-task dir, confirmation prompts, `--list` | `description`, `envFile`, `confirm`, a CLI `list` command |
| **Rundeck / Jenkins jobs** | Job options with allowed values, *remote option values* fetched from a URL/command, secure options, execution history | `choicesFrom`, the `secret` type, run history |
| **GitHub Actions / Azure Pipelines** | Steps with `if:` conditions, step outputs, re-run failed jobs, cron triggers | Workflow `when`, `::set` outputs, re-run from the failed step |
| **Windows Task Scheduler / cron** | Time triggers that fire with nothing else running | Optional export target for schedules |
| **npm scripts** | Zero-ceremony names for commands, pre/post hooks | Inline `command` entries; `dependsOn` covers pre-hooks |
| **Stream Deck / launchers** | One press, global hotkeys, icons | `hotkey`, `icon`, jump list, command palette |
| **Windows Terminal** | Profiles, per-profile working dir and env | "Open terminal here" with the script's resolved env |

Takeaways:

1. The **shared vs personal** split (JetBrains) is the core of the app and
   most tools do it poorly. Customisations storing only differences (§3.7) is
   the improvement: a shared script that changes does not orphan its personal
   copies.
2. **Discovered choices** (Rundeck) remove the most typing. Target and app
   names are exactly what users otherwise type from memory.
3. **Background-task readiness** (VS Code) makes servers pleasant: "Serve"
   turns green when the ready line appears, and its URL becomes a button.
4. None of these tools makes **exit codes** prominent. BatchPad should: a
   result badge per script and a history of passes and failures.

## 7. Feature catalogue

Priority: **M** = MVP (first usable build), **1** = v1, **10** = phase 10
(telemetry and agents), **L** = later, **✗** = considered and rejected.
Everything marked M, 1 or 10 is built: the MVP in phases 1–7, v1 in phases 8
and 9, and telemetry and agents in phase 10 (§10). L items are phase 11 or
later.

| Feature | Pri | Notes |
|---|---|---|
| Load workspace `batchpad.json`, global library, My Scripts | M | |
| Tree view with the three roots, filter box | M | |
| Runners: batch, python, csharp, powershell, exe | M | |
| Parameters: flag, choice, text, int, path | M | Form generated from definitions |
| Command preview + copy | M | |
| Captured output, live, stderr tinted, exit code, duration | M | |
| Stop: Job Object per run, polite close for GUI apps, then terminate | M | §4 |
| Per-target quoting (argv and cmd escaping), per-line UTF-8/OEM decoding, long paths | M | Unit-tested against real cmd |
| Workspace trust (§4.3) | M | Needed before anything runs from a cloned repo |
| Drag to My Scripts; customisations with values; folders; rename/delete | M | |
| Recent workspaces, open via command-line argument | M | |
| Window-mode runs | M | |
| Rich choices, `sharedParams`, `${param:x.field}`, `\|lower` filter | M | Build/run/package the same app with one picker |
| `choicesFrom` regex/list/glob | M | App, test and benchmark names come from the project |
| `multichoice` checklist, `emptyMeans`/`emptyArgs`, `maxPerCall` | M | Run all or some tests/benchmarks; named sets are My Scripts entries |
| Launching built apps (templated exe path, exe's folder as working dir) | M | |
| Long-running badge + stop companion | M | Local web server |
| `ready` pattern + open URL | M | Opens the served app in the browser |
| Duplicate / Save as new My Script, `nameTemplate` | M | Many named variants of one script |
| Sequential workflows: implicit and typed parameter flow, `forEach`, `when`, long-running last step | M | "Build & run", "run each benchmark", QA report `when: always` |
| `envVar` parameters, python `module` entries, segmented toggles | M | Headless/visible gameplay tests via pytest |
| `artifacts`: result files as links on the run tab, optional auto-open | M | QA report after a crawl sweep |
| Link nodes (URLs, local report files, folders) | M | |
| Links: file age and missing state, drag in from Explorer/browser, "Pin as link" | 1 | |
| Test results view from JUnit XML, "Re-run failed" | 1 | |
| Ask on run | 1 | |
| `choicesFrom` command | 1 | |
| Run history with logs, last-result badges | 1 | |
| Variables, `envFile`, env layering | 1 | Basic `${workspaceDir}` is M |
| `confirm`, `secret` | 1 | |
| Benchmark result capture (`--benchmark_out` JSON into history) and before/after comparison | L | |
| `errorPatterns`, clickable file:line, next/prev error | 1 | |
| ANSI colours | 1 | |
| Named locks, `singleInstance` | 1 | |
| `dependsOn` | 1 | |
| Live reload of config files | 1 | |
| Command palette | 1 | |
| Script folders: auto-discovery, live, include/exclude, hide, New badge | M | §3.10 |
| Detection: name, description, batch usage/`%~n` parameters | M | Proposals only |
| Detection: `argparse` via `ast`, PowerShell `param()` | 1 | |
| New workspace wizard (scan, checklist, propose folders, trust) | M | Pair suggestions came with v1 detection |
| Rename tracking and orphan re-attach for discovered scripts | 1 | |
| New script from template, drag in from Explorer (reference or copy) | 1 | |
| Detection: long-running and serve/stop pairs, change badges | 1 | |
| Library imports in global.json | 1 | |
| `include` in workspace files | 1 | |
| CLI: `batchpad run <id> [--set name=value] [--yes]`, `batchpad list` | 1 | The same definitions headless, for terminals and coding agents; exit code passed through (via `batchpad.com`, §9.3) |
| JSON Schema for config files (editor completion) | 1 | Published in-repo |
| Script editor: General, Parameters, Choices tabs; live preview; Test run | M | §5.1; what U1–U10 need |
| Choice-source picker: files in a folder, "words on this line", fixed list, regex with live preview | M | Covers `GAMES=`-style lists without writing a regex |
| Choice-source picker: derive a pattern from a selected example; output of a script | 1 | |
| Workspace settings page, shared-parameter editor | M | |
| Script editor: After run (ready pattern, open URL, stop companion, artifacts, test report) and Advanced (fixed args, name template) | M | U2–U4 need them |
| Script editor: Environment tab, ready-pattern tester, error patterns, argument template | 1 | Every field editable in the UI |
| Share with workspace / Copy to My Scripts | 1 | |
| Deterministic, atomic JSON writer; reload and prompt on external change | M | |
| Reload-and-reapply merge of an open edit | 1 | |
| Workflows: `continueOnError`, `failFast` | 1 | §4.1 |
| Workflow editor: steps, reorder, per-step values, workflow parameters, for-each, `when` | M | Parallel groups, retry and outputs in 1 |
| Workflows: parallel groups, step outputs, retry, re-run from failed step | 1 | |
| Schedules in-app: cron/every/at, tray, missed-run policy, Schedules view | 1 | §4.2 |
| Triggers: fileChanged, onStart, afterRun | 1 | |
| Export time schedules to Windows Task Scheduler | L | Needs the CLI |
| Run events, Insights view, `batchpad stats` | 10 | §4.4 |
| Telemetry sinks: jsonl (+ outbox), OTLP, Elasticsearch, InfluxDB, HTTP | 10 | §4.4; off by default, settings only |
| Agent CLI: `list --json`, `run --json`/`--errors-only`, `log`, agent attribution | 10 | §4.5 |
| Machine-wide named locks and `singleInstance` | 10 | §4.5; needed for parallel agents |
| MCP server (`batchpad mcp`) | 10 | §4.5 |
| Tray icon, keep running in the tray, failure notifications | 1 | §4.2 |
| Toasts, taskbar progress, jump list | L | |
| Hotkeys (global) | L | |
| Portable mode | 1 | Cheap, and helps sharing |
| Up-to-date checks (Taskfile `sources`/`generates`) | ✗ | That is a build system's job |
| Remote/SSH execution | ✗ | |
| Cross-platform UI | ✗ | Windows-native is a goal |
| Plugin system | ✗ | Runners are a small closed set; revisit if requested |

## 8. Technology

- **.NET 10, WPF**, `ThemeMode="System"` (the Fluent theme built into
  .NET 9+). It ships with Windows and needs no runtime installer when
  published self-contained. It looks native on Windows 11, and it is mature
  for TreeView, drag and drop and text-heavy panels.
  - *WinUI 3* was rejected: unpackaged deployment still drags in the
    Windows App SDK runtime, which works against "one exe you can share".
  - *Avalonia* was rejected: its cross-platform reach is not needed, and it
    looks less native.
- **CommunityToolkit.Mvvm** for view models; **System.Text.Json** for config.
- **Distribution:** `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`,
  which produces one exe. Any installer (winget/MSIX) comes later.
- **Tests:** MSTest.
  - The core library: config parsing, resolution, argument assembly,
    variable expansion, runner command lines, and process running against
    small fixture scripts.
  - View models, tested without a window.
  - A few FlaUI tests that drive the real exe through automation ids.

### Solution layout

```
BatchPad.slnx
src/BatchPad.Core/     net10.0         model, config IO, resolution, argument assembly, runner, history
src/BatchPad.App/      net10.0-windows WPF UI; view models over Core
src/BatchPad.Shim/     net10.0         `batchpad.com` console stub for the CLI (v1, §9.3)
tests/BatchPad.Core.Tests/
samples/               demo workspace with one script per runner
docs/
```

Core has no WPF dependency, so the CLI and the tests exercise the same code
the UI does.

## 9. Open questions (with defaults)

1. **Where does My Scripts live?** Default: `%APPDATA%`, keyed by workspace id.
   The alternative is a git-ignored `.batchpad/user.json` in the repo, which
   is easier to find but leaves a file in someone else's checkout. Revisit if
   users want to sync favourites through the repo.
2. **Is My Scripts per workspace or global?** Default: per workspace, because
   customisations reference workspace ids. Global scripts can be customised
   inside any workspace's My Scripts.
3. **One exe or App + CLI?** *Decided:* `BatchPad.exe` (GUI) contains all
   the logic. A tiny console-subsystem `batchpad.com` sits beside it: it runs
   `BatchPad.exe` with the `run`/`list` verbs, relays stdout, stderr and the
   exit code, and waits. This is needed because cmd does not wait for a
   GUI-subsystem exe, so `%errorlevel%` would be wrong. Typing `batchpad`
   finds the `.com` first.
   `run` takes an id (`global:` references too; a workspace id wins over a
   My Scripts one) or a unique name. The workspace is `--workspace` or the
   nearest `batchpad.json` at or above the current folder, never the most
   recent one, so a script runs against the repository it was typed in.
   Usage errors and unknown ids exit 2, a run refused before it starts
   (untrusted, unconfirmed, a missing value) exits 1, and a workflow exits
   with its last failed step's code. Output is relayed as UTF-8.
4. **One config file or one file per script?** Default: one `batchpad.json`
   (plus `include`s), written deterministically. The alternative is
   per-script sidecar files in `.batchpad/`, which would mean fewer merge
   conflicts when many people edit at once. Revisit if conflicts show up in
   practice.
5. **Unsaved values on shared scripts?** Default: kept for the session per
   script, and offered as "Save as My Script".

## 10. Roadmap

1. **Skeleton:** solution, Core model, config loading and the deterministic
   writer, script-folder discovery, and a WPF shell showing the three trees
   from a sample workspace.
2. **Run:** runners, per-target quoting, decoding, Job Objects and stop,
   captured output panel, exit codes, command preview, workspace trust.
3. **Parameters:** generated form, argument assembly, variables, rich and
   discovered choices, shared parameters, multichoice, segmented toggles.
4. **Editing:** the script editor (General, Parameters, Choices, After run,
   Advanced), the choice-source picker (MVP sources), workspace settings,
   shared parameters, detection proposals (name, description, batch
   parameters), New link, the minimal New workspace wizard.
5. **My Scripts:** drag and drop, customisations (including `stepValues`),
   persistence, folders, rename, delete, duplicate, `nameTemplate`, broken
   references.
6. **Chaining & launching:** sequential workflows (typed values, `forEach`,
   `when`, long-running last step) and the workflow editor, launching built
   exes, long-running runs with `ready`, open URL, stop companions, artifacts.
7. **MVP polish:** recent workspaces, window mode, filter, keyboard, packaging
   as a single exe. *The MVP is usable on a real project here: §11's U1–U10
   are set up entirely in the UI.*
8. **v1:** the remaining editor tabs and picker sources,
   `argparse`/PowerShell detection, rename tracking, New script, Explorer
   drag-in, history, locks, `dependsOn`, ask on run, test results, error
   navigation, ANSI, live reload with merge, command palette, CLI and
   `batchpad.com`, JSON schema, portable mode.
9. **Workflows & schedules:** parallel groups, step outputs, retry, re-run
   from the failed step, the in-app scheduler with a tray icon, the Schedules
   view, triggers. It builds on the history and locks from phase 8.
10. **Telemetry and agents:** run events and the Insights view, the `jsonl`
    sink and the outbox, the agent command line (`--json`, `--errors-only`,
    `log`, `stats`, agent attribution), machine-wide locks, the OTLP,
    Elasticsearch, InfluxDB and HTTP sinks, and the MCP server (§4.4, §4.5).
11. **Later:** benchmark comparison, Task Scheduler export, Windows shell
    integration (jump lists, taskbar progress). Open items that may join it:
    unattended secrets from Windows Credential Manager, and a CLI command to
    trust a workspace.

Phases 1–10 are done; phase 11 remains. Each phase leaves the app building
and runnable, and is broken into implementation steps when it starts.

## 11. Worked example: a multi-app game repository

The example is a C++ repository with a handful of games (SpaceTrader,
RallyRacer, RobotManager, KartRacer, BikeTrials, …), a dozen unit test
suites, a few benchmark exes, a web build of some of the games, and
pytest-driven gameplay scenarios and a UI crawler. Its `batchpad.json` lives
in that repository. This section records what such a repository needs from
BatchPad, and it is the acceptance test for the MVP; the test fixture
`tests/fixtures/adoption` is a stand-in for it.

### 11.1 What the user must be able to do

| # | Task | Mechanisms |
|---|---|---|
| U1 | Build any app (or all) in Debug/Release/Retail | shared `config` + `app` params, discovered choices |
| U2 | **Build latest release and run SpaceTrader**, saved as a one-click favourite, and the same for other apps under other names | workflow, launching built apps, customisations, `nameTemplate`, Duplicate |
| U3 | Build an app for the web and serve it locally, with the browser opening on it | workflow, long-running, `ready` + open URL, stop companion |
| U4 | Build a web deployment package (folder + zip) for an app | `webApp` param, `artifacts` (open the output folder) |
| U5 | Build and run all unit tests, or a picked set | multichoice with `emptyMeans: all`, saved sets |
| U6 | Build and run all benchmarks, or a picked set, at a tier (Smoke/Deep/All) | `forEach`, rich choices |
| U7 | Run the gameplay simulation scenarios, all or some, **headless or with the window visible** | workflow with an ASan build step, python `module`, segmented toggle, multichoice |
| U8 | Run the QA crawler / use-case tests, which try to reach every game action, over one, several or all games, then open the QA report | `forEach` (one game per invocation), `valueTransform`, `when: always`, `artifacts` |
| U9 | Keep links to the QA report, other local reports and web pages | link nodes |
| U10 | Every other build/packaging script (generate, assets, library patches, compile_commands) | plain script entries |

### 11.2 Script inventory

| Script | Parameters / behaviour |
|---|---|
| `generate.bat` | none; regenerates the solution |
| `build.bat` | `--release`/`--final`/`--asan`, then `--games`/`--tests`/`--tools`/`--bench`/`--assets` or names |
| `tools/build_locked.ps1` | forwards to `build.bat` under a lock (made redundant by `lock`) |
| `tools/regen_assets.py` | `--force`, `--only`, `--config` |
| `tools/generate_compile_commands.py` | none, after `generate.bat` |
| `tools/patches/apply.py` | `--check` |
| `tools/run_tests.py` | match filters, `--jobs N`, `--gated`, `--release`, `--asan`, `--no-shard`, `--slowest N` |
| `build_web.bat` | optional app |
| `package_web.bat` | app |
| `serve_web.bat` / `stop_web.bat` | shared port |
| `build/<config>/bin/<app>.exe` | the built apps themselves |
| `build/release/benchmarks/<bench>.exe` | `--benchmark_filter=^Smoke` or `^Deep`, repetitions |
| `pytest` in `tools/qa/scenarios/` | scenario files, `--game` (one per invocation), `--show-windows`, `--with-sound`, `--no-screenshots`, `--max-screens`, `--max-depth` |
| `tools/qa/report.py` | none; writes `qa_report.html` |

### 11.3 Sketch of its `batchpad.json`

This is what the UI would produce, shown to check that the mechanisms fit
together. Nobody is meant to type it. With script folders on the repo root
(`*.bat`) and `tools/`, most plain entries (generate, regen assets, library
patches and so on) are discovered and need no entry at all. Below are only
the entries that carry settings. The adoption tests build this file in the
editors and check it against the fixture's scripts.

```jsonc
{
  "name": "Game Studio",
  "scriptFolders": [
    { "path": ".", "include": ["*.bat"], "recurse": false },
    { "path": "tools", "include": ["*.py", "*.bat", "*.ps1"], "exclude": ["test_*", "*_lib.py"] }
  ],
  "sharedParams": {
    "config": { "type": "choice", "default": "--release", "choices": [
      { "value": "",                    "label": "Debug",        "dir": "debug",        "testFlag": "" },
      { "value": "--release",           "label": "Release",      "dir": "release",      "testFlag": "--release" },
      { "value": "--final",             "label": "Retail",       "dir": "retail",       "testFlag": "" },
      { "value": "--asan",              "label": "Debug ASan",   "dir": "debugasan",    "testFlag": "--asan" },
      { "value": "--release --asan",    "label": "Release ASan", "dir": "releaseasan",  "testFlag": "--release --asan", "split": true } ] },
    "app":    { "type": "choice", "choicesFrom": { "file": "build.bat", "regex": "set \"GAMES=([^\"]*)\"", "split": " " } },
    "webApp": { "type": "choice", "choicesFrom": { "file": "CMakeLists.txt", "regex": "add_game_app\\((\\w+)", "all": true } },
    // --game takes the exe name: the GAMES entry lowercased (robotmanager, not the src/robots folder)
    "game":   { "type": "multichoice", "emptyMeans": "all",
                "choicesFrom": { "file": "build.bat", "regex": "set \"GAMES=([^\"]*)\"", "split": " ", "valueTransform": "lower" } },
    "window": { "type": "choice", "choices": [
      { "value": "", "label": "Headless" }, { "value": "--show-windows", "label": "Visible" } ] }
  },
  "scripts": [
    { "folder": "Build", "items": [
      { "id": "generate", "path": "generate.bat" },
      { "id": "build", "name": "Build", "path": "build.bat", "lock": "native-build",
        "params": [ { "use": "config", "split": true },
                    { "use": "app", "name": "apps", "type": "multichoice", "emptyMeans": "all", "maxPerCall": 9 } ] }
    ]},
    { "folder": "Run", "items": [
      { "id": "run-app", "name": "Run app", "runner": "exe", "longRunning": true,
        "path": "build/${param:config.dir}/bin/${param:app|lower}.exe",
        "params": [ { "use": "config", "emit": false }, { "use": "app", "emit": false } ] },
      { "id": "build-run", "name": "Build & run",
        "nameTemplate": "Build ${param:config.label} & run ${param:app}",
        "params": [ { "use": "config" }, { "use": "app" } ],
        "steps": [ { "id": "build", "run": "workspace:build", "values": { "apps": ["${param:app}"] } },
                   { "id": "run",   "run": "workspace:run-app" } ] }
    ]},
    { "folder": "Web", "items": [
      { "id": "build-web", "name": "Build web", "path": "build_web.bat", "params": [ { "use": "webApp" } ] },
      { "id": "serve", "name": "Serve web build", "path": "serve_web.bat", "longRunning": true, "stop": "stop-web",
        "params": [ { "name": "port", "type": "int", "default": 8123 },
                    { "use": "webApp", "emit": false } ],
        "ready": { "pattern": "Press Ctrl\\+C to stop", "open": "http://localhost:${param:port}/${param:webApp}.html" } },
      { "id": "stop-web", "name": "Stop web server", "path": "stop_web.bat",
        "params": [ { "name": "port", "type": "int", "default": 8123 } ] },
      { "id": "web-local", "name": "Build web & serve locally",
        "params": [ { "use": "webApp" } ],
        "steps": [ { "id": "build", "run": "workspace:build-web" }, { "id": "serve", "run": "workspace:serve" } ] },
      { "id": "package-web", "name": "Package for web deploy", "path": "package_web.bat",
        "params": [ { "use": "webApp" } ],
        "artifacts": [ { "path": "build/package_web/${param:webApp}", "open": "onSuccess" } ] }
    ]},
    { "folder": "Test", "items": [
      { "id": "unit-tests", "name": "Build & run unit tests",
        "params": [ { "use": "config" },
                    { "name": "suites", "type": "multichoice", "emptyMeans": "all",
                      "choicesFrom": { "file": "build.bat", "regex": "set \"TESTS=([^\"]*)\"", "split": " " } } ],
        "steps": [ { "id": "build", "run": "workspace:build",
                     "values": { "apps": "${param:suites}" }, "emptyArgs": ["--tests"] },
                   { "id": "run", "run": "workspace:run-tests",
                     "values": { "match": "${param:suites}", "cfg": "${param:config.testFlag}" } } ] },
      { "id": "run-tests", "name": "Run unit tests", "path": "tools/run_tests.py",
        "params": [ { "name": "match", "type": "multichoice", "emptyMeans": "all" },
                    { "name": "cfg", "type": "text", "split": true },
                    { "name": "gated", "type": "flag", "arg": "--gated" } ] },
      { "id": "benches", "name": "Build & run benchmarks",
        "params": [ { "name": "benches", "type": "multichoice", "emptyMeans": "all",
                      "choicesFrom": { "file": "build.bat", "regex": "set \"BENCH=([^\"]*)\"", "split": " " } },
                    { "name": "tier", "type": "choice", "choices": [
                      { "value": "^Smoke", "label": "Smoke" }, { "value": "^Deep", "label": "Deep" },
                      { "value": "", "label": "All" } ] } ],
        "steps": [ { "id": "build", "run": "workspace:build",
                     "values": { "config": "--release", "apps": "${param:benches}" }, "emptyArgs": ["--bench"] },
                   { "id": "each", "forEach": "${param:benches}", "run": "workspace:run-bench",
                     "values": { "bench": "${item}" } } ] },
      { "id": "run-bench", "name": "Run one benchmark", "runner": "exe",
        "path": "build/release/benchmarks/${param:bench|lower}.exe",
        "params": [ { "name": "bench", "type": "text", "emit": false },
                    { "name": "tier", "type": "text", "arg": "--benchmark_filter=" } ] },
      { "id": "scenarios-run", "name": "Run gameplay scenarios", "runner": "python", "module": "pytest",
        "workingDir": "tools/qa",
        "args": ["--junitxml=${workspaceDir}/build/qa/scenarios.xml"],
        "testReport": "build/qa/scenarios.xml",
        "params": [ { "name": "files", "type": "multichoice", "emptyMeans": "all", "emptyArgs": ["scenarios"],
                      "choicesFrom": { "glob": "tools/qa/scenarios/test_*.py",
                                       "relativeTo": "tools/qa" } },
                    { "use": "app", "name": "game", "arg": "--game", "valueTransform": "lower" },
                    { "use": "window" }, { "name": "sound", "type": "flag", "arg": "--with-sound" } ] },
      // the scenario fixture drives the ASan build by default
      { "id": "scenarios", "name": "Gameplay scenarios",
        "params": [ { "use": "app", "name": "game", "valueTransform": "lower" }, { "use": "window" } ],
        "steps": [ { "id": "build", "run": "workspace:build", "values": { "config": "--asan", "apps": ["${param:game}"] } },
                   { "id": "run", "run": "workspace:scenarios-run" } ] },
      { "id": "qa-game", "name": "QA crawl one game", "runner": "python", "module": "pytest",
        "workingDir": "tools/qa", "args": ["scenarios/test_crawl.py"],
        "params": [ { "name": "game", "type": "text", "arg": "--game" }, { "use": "window" } ] },
      { "id": "qa-report", "name": "QA report", "path": "tools/qa/report.py",
        "artifacts": [ { "path": "qa_report.html", "open": "onSuccess" } ] },
      { "id": "qa-sweep", "name": "QA crawl & use cases",
        "params": [ { "use": "game" }, { "use": "window" } ],
        "steps": [ { "id": "build", "run": "workspace:build", "values": { "config": "--asan", "apps": "${param:game}" },
                     "emptyArgs": ["--games"] },
                   { "id": "crawl", "forEach": "${param:game}", "run": "workspace:qa-game", "values": { "game": "${item}" } },
                   { "id": "report", "run": "workspace:qa-report", "when": "always" } ] }
    ]},
    { "folder": "Reports", "items": [
      { "name": "QA report", "url": "qa_report.html" },
      { "name": "Local web build", "url": "http://localhost:8123/" }
    ]}
  ]
}
```

Notes on how the example's scripts fit:

- `build.bat` reads only `%~1`…`%~9`, so `apps` sets `maxPerCall: 9`. An empty
  selection has to become `--tests`, `--bench` or `--games`, never nothing,
  because with no arguments `build.bat` builds everything. Each workflow step
  overrides this with `emptyArgs`.
- `build.bat` takes the config switch first, and `--release --asan` must be
  two arguments, hence `split` on that choice.
- `run_tests.py` has no config value, only `--release`/`--asan` flags. The
  config's `testFlag` field carries them, so the tests run the build they
  just made.
- The scenario and QA fixtures drive the ASan build by default and take
  `--game <exe name>`. Folder names do not match exe names (`src/robots` is
  `robotmanager`), so `game` lists the `GAMES` entries lowercased.
- pytest runs from `tools/qa`, where the scenarios' conftest lives, so
  scenario choices are made relative to that folder.
- `serve_web.bat` prints `Serving … on http://…` and then `Press Ctrl+C to
  stop.` Either works as the ready pattern. `build_web.bat` and
  `serve_web.bat` end in `pause`, which captured mode's closed stdin handles.

### 11.4 The first favourite

"Build latest release and run SpaceTrader" means: drag **Run › Build & run** into
My Scripts, pick *Release* and the app, and the entry names itself
*Build Release & run SpaceTrader*. Rename it if you like. Stored in `user.json`:

```jsonc
{ "base": "workspace:build-run", "name": "Build latest release and run SpaceTrader",
  "values": { "config": "--release", "app": "SpaceTrader" } }
```

"Latest" means the current working tree. `build.bat` is incremental, so this
rebuilds only what changed and then launches
`build/release/bin/spacetrader.exe`. If the build fails, nothing launches and the
run shows the failing step. Duplicate the entry and change the app for the
next one.

The MVP is done when U1–U10 work from BatchPad with correct exit codes,
**set up entirely in the app, without opening a JSON file**. The same goes
for adding a new script: save it into a script folder, accept the detected
parameters, and it is ready to run.

