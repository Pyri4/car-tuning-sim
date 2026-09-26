# Architecture

## Status
**Decided (2026-09-26).** Engine/framework: **Godot 4.7 (.NET edition) for presentation, with an
engine-agnostic C# (.NET 8) simulation core.** Sections below marked *(planned)* describe intended
structure that does not exist yet; everything else reflects the repository as it is.

---

## 1. Engine / framework decision

### Requirements that drove the decision
The game is simulation-first: a deterministic mechanical model (engine, drivetrain, tires, damage)
matters more than rendering. The team is small and development is heavily AI-assisted. Content must
be data-driven and moddable. The mechanical model must be testable without launching the game.

### Candidates evaluated

| Criterion | Unity 6 | Unreal 5 | Godot 4.7 (.NET) | Custom (Rust/Bevy, C++/raylib) |
|---|---|---|---|---|
| Vehicle physics | WheelCollider (limited); serious sims replace it | Chaos Vehicles (good, opinionated) | VehicleBody3D (basic); Jolt rigid bodies | Write everything |
| Custom physics integration | Good (FixedUpdate, C#) | Good (C++), heavy | Good (`_PhysicsProcess`, C#, configurable tick) | Full control |
| 3D rendering | Very good | Best | Adequate (Forward+/Vulkan) | Must build |
| Editor tooling / UI | Mature | Mature, heavy | Good, lightweight, Control-node UI is strong for data-heavy screens | None |
| Asset pipeline | Mature; GUID `.meta` files | Binary `.uasset` | Text `.tscn`/`.tres`, glTF import | Must build |
| Modding | Needs third-party injection or asset bundles | Hard (pak/UGC tooling) | Loose files, PCK loading, open source | Whatever we build |
| Save / data-driven | Fine (C#) | Fine (C++/USTRUCT) | Fine (C#, System.Text.Json) | Fine |
| Iteration speed | Medium (domain reloads) | Slow (C++ compile) | Fast | Fast for logic, slow for tooling |
| AI-assisted development | Scenes/prefabs are YAML with GUIDs; editor-centric | Binary assets; blueprints opaque to text tools | Everything is text; C# is statically typed | Text, but huge surface to author |
| Headless CI / tests | Requires license activation | Heavy | `--headless` works; core tests need no engine at all | Easy |
| License / long-term risk | Proprietary; 2023 runtime-fee episode | 5% royalty > $1M | MIT, no royalties, source available | None |
| Small-team maintainability | Good | Poor for a small team | Good | Poor (engine maintenance cost) |

### Decision
**Godot 4.7 .NET (C#) as the presentation/runtime engine**, with the entire mechanical simulation in
a **pure .NET 8 class library (`CarSim.Core`) that has zero Godot references**.

Rationale:
1. The simulation is the product. Keeping it in a plain .NET library means it is tested with
   `dotnet test` in seconds, runs identically in the game, the CLI tools and CI, and is portable to
   another C# engine (Unity) if Godot ever becomes a limitation. This sharply reduces engine lock-in.
2. C# gives static typing, value types and JIT performance for per-tick math (tire/drivetrain at
   hundreds of Hz), which GDScript does not.
3. Godot's text-based scenes and resources are diff-able and reviewable, which suits AI-assisted
   development and code review.
4. MIT license, no royalties, source available (we can patch or build the engine ourselves — which we
   already had to do, see §8).
5. Godot's weaknesses (3D fidelity below UE/Unity, a simplistic built-in vehicle body) hit our lowest
   priorities. We intend to write our own tire and drivetrain model anyway; Godot supplies rigid-body
   integration (Jolt), collision, rendering, input and UI.
6. Modding: base content ships as loose JSON next to the executable (§5), the same format mods will
   use.

Rejected: Unreal (binary assets, heavy iteration, overkill for the team size); Unity (licensing risk,
editor-centric GUID-heavy serialization, license-gated headless CI; otherwise a reasonable second
choice — and the core stays portable to it); custom engine (tooling cost dwarfs the benefit).

### Versions
- Godot **4.7.2-stable** (.NET edition) — GodotSharp targets `net8.0`.
- .NET SDK **8.0** (`global.json` pins major 8, rolls forward within it).
- Test framework: **xUnit**.

---

## 2. Repository layout

```
CarTuningSim.sln
Directory.Build.props      shared compiler settings (nullable, warnings-as-errors, LangVersion)
global.json                .NET SDK pin
content/base/              base-game content as JSON (parts, engines, fuels, tunes, vehicles)
src/CarSim.Core/           engine-agnostic simulation + domain model (NO Godot references)
tests/CarSim.Core.Tests/   xUnit tests for the core
tools/CarSim.Cli/          headless command-line tool (inspect, validate, dyno) built on the core
game/                      Godot 4.7 project (presentation layer); references CarSim.Core
```

`CarSim.Core` must never reference Godot. The game project references the core; the reverse is
forbidden. This is enforced structurally: the core project has no Godot package reference.

---

## 3. Layer boundaries

### Simulation core (`CarSim.Core`)
Deterministic, fixed-timestep, SI units internally. No rendering, no input, no file-format concerns
beyond the content loader. Namespaces:

- `CarSim.Core.Common` – unit conversions, physical constants, interpolation tables, root finding.
- `CarSim.Core.Parts` – part definitions (typed per category), part instances (wear), spec registry.
- `CarSim.Core.Content` – JSON content loading, validation and the content database.
- `CarSim.Core.Fuels` – fuel definitions.
- `CarSim.Core.Engines` – engine family definitions (slot graph), engine assembly (install/remove
  order), compatibility validation, derived geometry, valvetrain limits.
- `CarSim.Core.Ecu` – editable tunes (tables + calibration) and the runtime ECU controller.
- `CarSim.Core.Simulation` – the mean-value engine model: configuration, air path, fuel system,
  combustion, thermal, lubrication, telemetry.
- `CarSim.Core.Dyno` – dyno runs and telemetry capture.
- `CarSim.Core.Damage` *(planned)* – stress evaluation, fatigue accumulation, failures and diagnostic reports.
- `CarSim.Core.Vehicles` *(planned)* – drivetrain, gearbox, differential, tires, chassis state.

### Domain / data
Content (parts, engines, fuels, tunes, vehicles) is JSON under `content/`. Definitions are immutable
after load. Runtime state (installed parts, wear, fatigue) lives in separate instance objects so
definitions can be shared.

### Gameplay *(planned)*
Garage, inventory, money, jobs, save/load. Will live in a separate pure C# project
(`CarSim.Gameplay`) so it stays testable without Godot.

### Physics
The engine and drivetrain models are ours (in the core). Vehicle rigid-body motion and collision will
use Godot/Jolt; tire forces will be computed by the core and applied as forces to Godot bodies
through a thin adapter in `game/`. *(planned)*

### Presentation (`game/`)
Godot scenes and C# scripts that display core state and send player intent to the core. Scripts
must not contain mechanical formulas; if a UI needs a number, the core exposes it.

### Tools
- `tools/CarSim.Cli` – validate content, inspect assemblies, run a dyno sweep and print/CSV-export
  results. Used for development and as a regression harness.
- Dyno, telemetry and debugging views in the game UI. *(in progress)*

---

## 4. Simulation architecture

- **Units:** SI internally (m, kg, s, Pa, K, rad/s, N·m, W). Content files use conventional
  automotive units with the unit in the field name (`bore_mm`, `flow_cc_min`, `max_pressure_bar`);
  definitions convert to SI once at load time and expose SI properties to the simulation.
- **Determinism:** no randomness in the core model. Any future variation (manufacturing tolerance)
  must come from explicit seeded inputs so tests stay deterministic.
- **Time stepping:** the engine model is a *mean-value* model advanced with a fixed `dt`. Fast
  algebraic relationships (air path, fueling) are solved quasi-statically each step; slow states
  (temperatures, turbo shaft energy, engine speed when not held, damage) are integrated.
- **Data flow:** `Assembly (parts + instances) → EngineConfiguration (validated, derived specs) →
  EngineSimulation (state + step) → EngineTelemetry (per-step outputs)`. Parts affect the model only
  through their spec properties; there are no part-ID special cases in simulation code.
- **Failure:** thresholds plus accumulated fatigue (see SIMULATION_SPEC.md). Every failure produces a
  structured report with cause, measured values, ratings and recommendations.

See SIMULATION_SPEC.md for equations and PARTS_DATABASE.md for the data schema.

---

## 5. Content and modding

- Base content lives in `content/base/` as JSON (comments and trailing commas allowed).
- The loader discovers every `*.json` file under a content root, reads each object's `category`
  (or document `kind`), and deserializes it into the typed definition for that category.
- Shipping plan: exported builds carry `content/` as loose files beside the executable. Mods will be
  additional content roots loaded after `base` (override-by-id) *(planned)*.
- New part *categories* require code (the simulation must know what the properties mean); new
  *parts* in existing categories require only data.

---

## 6. Save/load *(planned)*
Runtime state (inventory, part instances with wear/fatigue, assemblies, tunes, money) serializes to
versioned JSON. Definitions are referenced by id, never embedded, so content updates flow into saves.

---

## 7. Testing strategy

- **Core unit tests** (`tests/CarSim.Core.Tests`, xUnit): geometry, unit conversions, airflow,
  fueling limits, combustion, knock, thermal, turbo, damage, compatibility, content validation.
  Deterministic; no Godot.
- **Behavioral tests** assert directional physics ("more boost → more airflow", "restrictive exhaust
  → less top-end power") rather than brittle exact numbers, plus a small set of pinned reference
  values for regression.
- **Content tests** load every file under `content/` and validate references, ranges and that the
  stock engine assembles and runs.
- **CLI** runs a full dyno sweep as a smoke test.
- **Godot**: the game project is compiled with `dotnet build` (Godot.NET.Sdk from NuGet) in CI; a
  headless Godot run of a smoke-test scene is used where a Godot binary is available.

---

## 8. Build environment notes

- `dotnet build CarTuningSim.sln` and `dotnet test` build and test everything except Godot runtime
  behaviour.
- In the development container used for the initial implementation, Godot release downloads were
  blocked by network policy, so the Godot 4.7.2 .NET editor was built from source
  (`scons platform=linuxbsd target=editor module_mono_enabled=yes`). This is an environment
  workaround, not a project requirement: normal development uses the official Godot 4.7.2 .NET build.

---

## 9. Decision log

| Date | Decision | Reason |
|---|---|---|
| 2026-09-26 | Godot 4.7 .NET + pure C# core | See §1 |
| 2026-09-26 | JSON content with unit-suffixed field names, SI at runtime | Moddable, explicit units, one conversion point |
| 2026-09-26 | Mean-value engine model, fixed timestep | Captures torque/airflow/thermal/turbo behaviour cheaply and deterministically; extensible |
| 2026-09-26 | Parts sold/installed as sets per slot (e.g. a set of 4 pistons), single condition per set | Keeps the first prototype small; per-cylinder state is a later extension |
| 2026-09-26 | Assembly order defined as data (`install_after` graph per engine family) | Disassembly/reassembly gameplay without hardcoded sequences |
