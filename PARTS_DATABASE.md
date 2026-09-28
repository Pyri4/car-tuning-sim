# Parts Database

## Purpose
The canonical data model for mechanical content. This document describes what the loader in
`src/CarSim.Core/Content/ContentLoader.cs` actually accepts. If the code and this file disagree,
the code is right and this file must be fixed.

## Files and loading
- Content lives under `content/<root>/` (base game: `content/base/`). Every `*.json` file below the
  root is loaded, in ordinal path order.
- A file is an object with any of these arrays: `parts`, `engines`, `fuels`, `tunes`, `scenarios`,
  `vehicles`.
- JSON is **snake_case**. `//` and `/* */` comments and trailing commas are allowed.
- **Unknown fields are errors** (catches typos such as `bore_m`). Missing required fields are errors.
- The loader collects *all* errors (file, item id, message) instead of stopping at the first.
- Ids must be unique within a layer (the base game, or one mod).
- Run `dotnet run --project tools/CarSim.Cli -- validate` to check content (and every mod).

## Mods
- Each folder under `content/mods/` (or `CARSIM_MODS_DIR`) is a mod, loaded after the base game in
  ordinal folder-name order. Files inside use exactly the base format.
- A mod can add anything (parts, engines, fuels, tunes, scenarios, vehicles) and can **redefine** an
  id from the base game or an earlier mod: the whole definition is replaced, and the loader lists every
  override (`carsim validate`, the Garage tab).
- Mod content goes through the same validation as base content, and cross-references (stock parts,
  scenario slots, ...) are checked after all layers load, so a mod may refer to base content and vice
  versa.
- Saves refer to parts by id: removing a mod whose parts are in a save gives a clear load error naming
  the missing id.
- Example: `docs/example-mod/club_clutch_pack/` adds a clutch and redefines a tyre. Copy the folder
  into `content/mods/` to load it; `carsim validate --mods docs/example-mod` checks it in place.

## Units convention
Field names carry their unit. The simulation converts to SI once, at load time.

| Suffix | Unit |
|---|---|
| `_mm` | millimetres |
| `_cc` | cubic centimetres |
| `_g`, `_kg` | grams, kilograms |
| `_bar`, `_kpa` | pressure (absolute unless the field says gauge) |
| `_c` | degrees Celsius |
| `_deg` | degrees (crank degrees unless stated) |
| `_cfm` | cubic feet per minute at the 28 inH₂O flow-bench standard |
| `_cc_min` | injector flow, cc/min |
| `_lph` | litres per hour |
| `_kn`, `_n`, `_nm` | kilonewtons, newtons, newton-metres |
| `_kg_m2` | kg·m² (rotational inertia) |
| `_w_per_k` | W/K (heat transfer conductance) |
| `_ms` | metres per second |

## Part (common fields)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `id` | string | yes | Unique id, e.g. `k20.pistons.forged_lc` |
| `name` | string | yes | Display name |
| `category` | string | yes | One of the categories below; selects the `spec` schema |
| `manufacturer` | string | no | Brand (fictional) |
| `description` | string | no | Player-facing text |
| `price` | number | no | New price, game currency |
| `mass_kg` | number | no | Mass as sold (whole set for set parts) |
| `provides` | string[] | no | Interface keys this part offers (e.g. `k20.deck`) |
| `requires` | string[] | no | Interface keys another installed part must provide |
| `tags` | string[] | no | Search/filter tags, not used for compatibility |
| `spec` | object | yes | Category-specific specification (below) |

### Set parts
Pistons, connecting rods and injectors are sold and installed as a set (`count` must equal the
engine's cylinder count). A set has one condition value. Per-cylinder state is future work.

## Adjustable settings (`adjustable`)
A part may declare which of its own numeric spec fields the owner can adjust once they have it (a
setup setting), with a range and step:

```json
"adjustable": {
  "front_camber_deg": { "min": -3.0, "max": 0, "step": 0.1, "label": "Front camber (camber plates)" },
  "ride_height_offset_mm": { "min": -50, "max": -5, "step": 5, "label": "Ride height (vs factory)" }
}
```

- Keys are spec field names; the part's authored value is the default and must lie in the range.
- The loader builds the spec at both ends of the range and validates it like authored content.
- Settings live on the part instance (they travel with the part and are saved); the simulation reads
  the part's *effective* spec, so an adjustment changes exactly what the physics already uses.
- Base content: tyre pressure (all tyres), front camber (OEM eccentric bolts; coilovers), ride height
  and damping (coilovers), anti-roll bars and rear camber (track coilovers), brake balance (big brake
  kit), LSD preload.

## Category specs

### `block`
`cylinders` (int), `bore_mm`, `deck_height_mm` (crank centreline → deck), `main_journal_diameter_mm`,
`max_cylinder_pressure_bar` (deck/head-bolt clamp limit), `coolant_capacity_l` (default 3.0),
`material`.

### `crankshaft`
`stroke_mm`, `main_journal_diameter_mm`, `rod_journal_diameter_mm`, `max_rpm` (torsional/bending
fatigue threshold), `max_torque_nm`, `inertia_kg_m2`, `material`.

### `main_bearings`, `rod_bearings`
`journal_diameter_mm`, `clearance_mm` (new oil clearance; wear increases it), `max_load_kn`,
`material`.

### `connecting_rods` (set)
`count`, `length_mm` (centre-to-centre), `big_end_diameter_mm`, `pin_diameter_mm`, `mass_each_g`,
`max_tensile_load_kn` (inertia load), `max_compressive_load_kn` (gas load), `beam`.

### `pistons` (set)
`count`, `bore_mm` (bore they are made for), `pin_diameter_mm`, `compression_height_mm`,
`dish_volume_cc` (positive dish adds chamber volume; negative = dome), `mass_each_g` (with pin and
rings), `max_cylinder_pressure_bar`, `max_crown_temperature_c`, `material`.

### `head_gasket`
`bore_mm` (fire-ring bore, ≥ cylinder bore), `compressed_thickness_mm`, `max_cylinder_pressure_bar`,
`material`.

### `cylinder_head`
`chamber_volume_cc`, `intake_port_flow_cfm` and `exhaust_port_flow_cfm` (flow-bench curves: arrays
of `[lift_mm, cfm]` per cylinder, lift strictly increasing), `valve_moving_mass_g` (valve + retainer +
follower + ⅓ spring), `valves_per_cylinder`, `material`, `valvetrain` (`dohc` default, `sohc`, `ohv`: must match the
camshafts serving the same bank).

### `camshafts` (intake + exhaust pair)
`intake_duration_deg`, `exhaust_duration_deg` (crank degrees at 1 mm lift), `intake_lift_mm`,
`exhaust_lift_mm`, `lobe_separation_deg` (cam degrees). Overlap is derived.

Cam timing (optional; see SIMULATION_SPEC.md, "Cam timing"): `intake_centerline_deg` (crank degrees ATDC) and
`exhaust_centerline_deg` (crank degrees BTDC) state where the lobes sit as installed — a degreed-in pair, or the
**park** position of cam phasers (intake fully retarded). Both or neither; without them the pair sits straight up
on its lobe separation, as every K20 cam does. `intake_phaser_range_deg` (default 0) is how far an intake phaser can
advance the intake cam from park; it needs the centrelines and an ECU with `cam_phase_control`.

`valvetrain` (`dohc` default, `sohc`, `ohv`) must match the head's on each bank it serves. A pushrod (OHV) camshaft
lives in the block: give it a `requires` on the block's cam tunnel and one slot serving every bank.

Variable valve lift (optional): `high_lift_profile` — `{intake_duration_deg, intake_lift_mm, exhaust_duration_deg,
exhaust_lift_mm}` — is the second cam profile a two-stage (VTEC-type) camshaft switches to. It needs an ECU with
`valve_lift_control` and the tune's `valve_lift_switch_rpm`; the springs must clear its lift.

### `valve_springs`
`seat_force_n`, `open_force_n` (force at full lift; sets valve-float speed), `max_lift_mm` (coil bind
/ retainer clearance).

### `intake_manifold`
- `runner_length_mm` (50–1000): the **acoustic length** of the primary runner stage, from the runner's mouth in the plenum
  (or the airbox) to the intake valve seat, **including the cylinder head's intake port**. Longer runners tune to lower
  speed.
- `runner_diameter_mm` (optional, 15–120): mean inner diameter of the runners (a round runner of the same mean
  cross-section). Without it the model assumes 0.40 × bore (assumption A5) and the validator reports
  `default_intake_geometry`.
- `flow_cfm` (includes filter/inlet).
- Variable intakes (need an ECU with `intake_runner_control`; without it the primary stage stays and the validator warns):
  - `switched_runner_length_mm` (50–1000): a two-stage intake's second stage, used above the tune's
    `intake_runner_switch_rpm`;
  - or `switched_stages`: any number of stages, `[{runner_length_mm, runner_diameter_mm?}, …]`, in the order the ECU
    selects them as speed rises (stage 1, 2, …; the primary is stage 0). A stage without its own diameter takes the
    manifold's. The tune's `intake_runner_switch_rpm` switches to stage 1 and `intake_runner_upper_switch_rpm`
    (ascending) to stage 2 onwards. Not together with `switched_runner_length_mm`.
- Not in the schema (deferred with the plenum mode): plenum volume.

### `throttle_body`
`bore_mm`, `flow_cfm` (wide open).

### `injectors` (set)
`count`, `flow_cc_min` (static flow per injector at `rated_pressure_kpa`, default 300),
`max_duty` (recommended maximum, default 0.85), `dead_time_ms` (opening delay per pulse, 0–3 ms, default 0;
constant — no battery-voltage or pressure dependence).

### `fuel_pump` (pump + manifold-referenced regulator)
`free_flow_lph` (flow at zero pressure), `max_pressure_kpa` (dead-head), `regulated_pressure_kpa`
(base pressure above manifold pressure).

### `exhaust_manifold`
`flow_cfm`, `primary_length_mm` (tunes scavenging speed), `scavenging_gain` (peak VE gain from pulse
tuning, 0–0.15).

### `exhaust`
`flow_cfm`, `diameter_mm`, `has_catalyst`.

### `oil_pump`
`displacement_cc_per_rev`, `relief_pressure_kpa`.

### `oil_pan`
`capacity_l`, `max_sustained_g` (before pickup starvation), `baffled`.

### `radiator`
`heat_rejection_w_per_k` (UA at `reference_air_speed_ms`, default 15 m/s), `coolant_capacity_l`,
`thermostat_open_c`.

### `flywheel`
`inertia_kg_m2`, `max_rpm`.

### `ecu`
`map_sensor_max_kpa` (absolute; above this the ECU cannot see load), `boost_control`,
`knock_control`, `max_rev_limit_rpm`, `tables_editable`, `cam_phase_control` (default false: can drive an intake cam
phaser from the tune's `intake_cam_advance_deg`; without it a phaser stays parked and the validator warns
`cam_phaser_uncontrolled`), `valve_lift_control` (default false: can switch a two-stage camshaft; else
`valve_lift_uncontrolled`), `intake_runner_control` (default false: can switch a variable intake; else
`intake_runner_uncontrolled`).

### `turbocharger`
`compressor_wheel_diameter_mm` (exducer; tip speed → pressure ratio), `compressor_choke_flow_kg_s`
(corrected choke flow at `max_shaft_rpm`; choke flow scales with √speed), `compressor_peak_efficiency`, `compressor_peak_efficiency_flow_kg_s`,
`compressor_peak_efficiency_pressure_ratio` (island centre), `compressor_surge_flow_at_pr2_kg_s`,
`turbine_flow_area_cm2` (effective nozzle area — the A/R trade-off), `turbine_wheel_diameter_mm`,
`turbine_peak_efficiency`, `rotor_inertia_kg_cm2`, `max_shaft_rpm`, `wastegate_spring_kpa` (gauge),
`wastegate_flow_area_cm2`, `max_turbine_inlet_temperature_c` (default 950), `ball_bearing`.
Turbos `require` a flange interface (`turbo_flange.t25`, `turbo_flange.t3`) and `provide`
`turbo.fitted` and `boost.source`.

### `intercooler`
`effectiveness` (at `reference_flow_kg_s`), `reference_flow_kg_s`, `flow_cfm` (pressure drop).
Requires `boost.source`.

### Chassis categories (vehicle slots)
- `clutch`: `max_torque_nm` (new, cold), `fade_start_c` (facing temperature where friction fades and
  wear accelerates; organic ≈ 250, cerametallic ≈ 450), `life_mj` (slip energy the facings absorb at
  normal temperature), `heat_capacity_j_per_k`.
- `gearbox`: `ratios` (forward, first gear first), `reverse_ratio`, `efficiency`, `max_torque_nm` (input),
  `shift_time_s`, `input_inertia_kg_m2`.
- `differential`: `final_drive_ratio`, `type` (`open` | `clutch_lsd` | `locked`), `preload_nm`,
  `locking_accel`, `locking_decel` (0–1), `max_torque_nm` (pinion/input torque rating; default 1200).
- `tires` (a pair, one axle): `width_mm`, `aspect_ratio`, `rim_diameter_in`, `peak_friction` (the
  compound at the tyre's nominal load, 3500 N per 205 mm of width), `load_sensitivity` (friction lost
  per doubling of load; construction/compound — width enters through the nominal load, so do not
  fudge it per size), `peak_slip_ratio`, `peak_slip_angle_deg` (the construction at a 205 mm tread;
  scaled by `(205/width)^0.5`), `rolling_resistance`, `inertia_kg_m2`
  (per wheel), `compound`, `tread_life_mj` (sliding energy the pair absorbs before it is worn out;
  softer compounds wear faster), `cold_pressure_kpa` (set in the garage; rises with temperature),
  `optimal_pressure_kpa` (hot), `optimal_temperature_c`, `temperature_window_c`,
  `temperature_grip_loss` (grip lost far outside the window), `thermal_mass_j_per_k` (per tyre).
- `suspension`: `front_spring_n_mm`, `rear_spring_n_mm` (wheel rates), `front_damper_ns_m`,
  `rear_damper_ns_m` (per wheel), `front_arb_nm_deg`, `rear_arb_nm_deg`, `ride_height_offset_mm`,
  `front_camber_deg`, `rear_camber_deg` (static; negative = top in), `front_camber_gain`,
  `rear_camber_gain` (fraction of body roll the geometry recovers).
- `brakes`: `front_max_torque_nm`, `rear_max_torque_nm` (per wheel at full pedal), `fade_start_c`
  (pad fade temperature; street ≈ 450, race ≈ 600), `rear_pressure_factor` (balance bar: rear line
  pressure relative to the front), `pad_life_mj` (energy per axle at normal
  temperature), `front_heat_capacity_j_per_k`, `rear_heat_capacity_j_per_k` (disc thermal mass).

## Vehicles (`vehicles`)
`id`, `name`, `description`, `engine` (the **stock** engine family: what the car ships with and what its curb mass
includes — not a restriction on which engines fit), `drivetrain` (`rwd`|`fwd`), `curb_mass_kg`
and `front_weight_fraction` (factory build), `wheelbase_m`, `track_front_m`, `track_rear_m`,
`cg_height_m`, `yaw_inertia_kg_m2`, `drag_coefficient`, `frontal_area_m2`, `max_steer_deg`,
`bump_travel_mm` (wheel travel from factory ride height to the bump stops, default 75; lowering
takes it away), `slots`, `stock_parts`.

Slot ids are free; the vehicle model finds each part by its role: exactly one required slot of each
of `clutch`, `gearbox`, `differential`, `suspension` and `brakes`, and one required `tires` slot per
axle, marked `"axle": "front"` / `"axle": "rear"` (`axle` is only valid on tyre slots).

Whether an engine fits a car is decided by interfaces (`VehicleCompatibility`): every chassis part's `requires` must be
provided by the engine's parts or the car's other parts. Gearboxes require their bellhousing pattern
(`k20.bellhousing`, `m54.bellhousing`), which the block provides — so an engine with another pattern needs a matching
gearbox (or, later, an adapter part). Checked when a scenario loads, when the garage builds the car and by
`carsim drive --vehicle`.

## Engine families (`engines`)

| Field | Meaning |
|---|---|
| `id`, `name`, `description` | Identity |
| `cylinders`, `layout` | `cylinders` must match the installed block; `layout` is `inline` (one bank), `v` (two or more banks) or `flat` (two banks) |
| `banks[]` | optional: `{id, cylinders: [1-based cylinder numbers]}` per bank, together covering 1..N once. Omitted = one bank `main` holding every cylinder |
| `bank_angle_deg` | optional: V engines (0, 180), flat engines 180, not on inline engines |
| `firing_order[]` | optional: a permutation of 1..N (validated data; no physics yet) |
| `slots[]` | `id`, `category`, `display_name`, `required` (default true), `install_after` (slot ids that must be installed first — also defines removal order), `accessible_in_vehicle`, `banks` (bank-scoped categories only: the bank ids the slot serves; omitted = every bank) |
| `stock_parts` | slot id → part id (factory build) |
| `stock_tune` | tune id (factory calibration) |

The `install_after` graph must be acyclic. A slot can be filled only when all of its
`install_after` slots are filled; it can be emptied only when no filled slot lists it.

The family must fit the engine model (`EngineTopology`), or it fails to load with a message naming the problem:
- **engine-wide** categories — `block`, `main_bearings`, `crankshaft`, `rod_bearings`, `connecting_rods`, `pistons`,
  `injectors`, `fuel_pump`, `oil_pump`, `oil_pan`, `radiator`, `flywheel`, `ecu` — exactly one required slot each,
  serving the whole engine (no `banks`). Per-cylinder parts are one set of N;
- **bank-scoped** categories — `head_gasket`, `cylinder_head`, `valve_springs`, `camshafts`, `intake_manifold`,
  `throttle_body`, `exhaust_manifold`, `exhaust` — every bank served by exactly one required slot of each; the optional
  `turbocharger` (empty = that bank is naturally aspirated) and `intercooler` serve each bank at most once. One slot may
  serve several banks (a pushrod V8's camshaft, a common plenum, one turbo for both banks of a flat-four);
- the architecture is consistent: bank cylinder lists partition 1..N, bank ids are unique, the layout agrees with the
  bank count, the bank angle suits the layout, the firing order is a permutation, slot banks exist.
Superchargers, dry sumps, direct injection and exhaust cam phasing need model work first (ENGINE_AUTHORING_GUIDE.md).

Other slots (categories the model does not read) are free-form.

## Fuels (`fuels`)
`id`, `name`, `octane_ron`, `octane_mon` (default RON − 10; RON − MON is the fuel's sensitivity, which
counts under boost), `stoichiometric_afr`, `lower_heating_value_mj_kg`, `density_kg_l`,
`charge_cooling_factor` (evaporative cooling per kg of stoichiometric charge relative to gasoline = 1.0; sets the latent heat `350 kJ/kg · factor · AFR/14.7`), `description`.

## Tunes (`tunes`)
See SIMULATION_SPEC.md (ECU section) — `rpm_axis`, `load_axis_kpa`, `target_lambda[load][rpm]`,
`ignition_advance_deg[load][rpm]`, `volumetric_efficiency[load][rpm]` (speed-density fuel map: VE
relative to the MAP and intake air temperature the ECU reads, 0.05–3.0), `displacement_cc` (the
engine size the ECU assumes), `injector_flow_cc_min`, `injector_dead_time_ms`, `fuel_stoich_afr`,
`fuel_density_kg_l` (the injector and fuel calibration: the ECU meters with these beliefs, never the installed
injectors or the fuel in the tank), optional `boost_target_kpa[rpm]` (closed loop on the ECU's MAP sensor above
80 % pedal), optional `intake_cam_advance_deg[load][rpm]` (crank degrees of intake advance from the phaser's park
position, 0–80, looked up at the MAP the ECU last read), optional `valve_lift_switch_rpm` and `intake_runner_switch_rpm`
(500–25,000: where a two-stage camshaft or variable intake switches, with 150 rpm hysteresis), `rev_limit_rpm`,
`idle_rpm`, `knock_control_enabled`.

The VE table, displacement, dead time and fuel density are required (saves older than version 4 get the
installed injectors' dead time and their fuel's density on load). Generate a base table for the build the tune is meant
for with `carsim calibrate-ve <engine> [build options]` (it prints the rows to paste); an engine whose
breathing differs from that build runs off its target λ until the table is re-tuned. For a new engine family the
base maps come from three dev tools, in this order: `calibrate-cams` (a phaser's schedule: the advance that traps the
most air at each point), `calibrate-ve`, `calibrate-spark` (`min(best-torque − 1°, knock limit − 1.5°)` on the fuel
given), then `calibrate-ve` once more.

## Scenarios (`scenarios`)
New-game starting points: `id`, `name`, `description`, `engine`, `vehicle` (optional car id; the engine must fit the
car by interfaces — any engine whose block provides the car's gearbox bellhousing, not only the car's stock engine),
`money`, `fuel`, `tune` (empty = the engine's stock tune), `wear`
(engine or chassis slot → 0–1), `fatigue` (slot → {failure_mode: 0–1}), `inventory` (part ids on the
shelf). Failure modes use snake_case names (`detonation`, `head_gasket_breach`, ...). With a vehicle,
the garage holds the car's stock chassis parts alongside the engine; the car can be driven only with
the engine installed in it, every required chassis slot filled, and a runnable, unseized engine.

## Compatibility
Two mechanisms, both validated by `AssemblyValidator`:

1. **Interfaces** (data): a part's `requires` keys must be `provides`d by another installed part.
   Used for mounting patterns (`k20.deck`, `k20.cam_carrier`, turbo flanges, bellhousings, ...). Bank-aware: a part
   serving some banks needs its keys from an engine-wide part or one serving one of the same banks (the right exhaust
   manifold's flange from the right head). Chassis parts' keys may come from the engine (a gearbox's bellhousing).
2. **Physical rules** (code reading spec values, never part ids):
   - set counts equal the cylinder count;
   - crank main journals = block saddles = main bearings; crank pins = rod big ends = rod bearings;
     piston pin = rod small end (±0.01 mm);
   - piston bore = block bore (±0.05 mm);
   - gasket bore ≥ cylinder bore (per bank);
   - head and camshaft valvetrain types match (per bank);
   - piston-to-head clearance > 0 (error), ≥ 0.6 mm (else warning); deck clearance ≤ 1.5 mm (else
     knock-prone warning) — per bank, since each bank has its own gasket and head;
   - compression ratio derived from each bank's geometry; warnings above 13.5:1 and below 7.5:1 (reported once when
     every bank agrees, per bank with the bank named when they differ);
   - cam lift (either profile) ≤ spring usable lift (coil bind, per bank);
   - variable hardware (phaser, two-stage cams, variable intake) needs the matching ECU output (warnings);
   - with a rev limit: valve-float speed, crank and flywheel ratings vs the limit (warnings).

Errors mean the engine cannot be started. Warnings mean it will run, with risk.

## Isar M54 reference engine
The second engine family is a real-engine validation target authored as data only: **the BMW M54B30 as fitted to the
European E46 330i/330Ci (2000–2006, Siemens MS43 engine management, not the ZHP package)**. One variant only — the
US rating (225 hp SAE) and the ZHP (175 kW, different cams, 6,800 rpm) are different specifications and are not
mixed in. In the game it follows the project's fictional-marque convention (Kestrel K20 ↔ Honda K20): "Isar M54",
content ids `isar_m54`, `m54.*`; the car is the Isar C30 coupé.

| Published figure | Value | Source |
|---|---|---|
| Displacement | 2979 cc | Wikipedia "BMW M54"; BMW "E85 M54 Engine" technical training; automobile-catalog.com (2000 BMW 330i) |
| Bore × stroke | 84.0 × 89.6 mm | same |
| Cylinders / arrangement | 6, inline | same |
| Valvetrain | DOHC, 4 valves per cylinder, double VANOS (intake and exhaust) | same |
| Compression ratio | 10.2:1 | same |
| Rated power | 170 kW (231 PS) at 5,900 rpm, on RON 98 | same; BMW Europe quotes rated output on RON 98 (owner's-manual fuel note, via PistonHeads/Whirlpool threads: 95 usable, 91 in emergencies) |
| Rated torque | 300 N·m at 3,500 rpm | same |
| Rev limit / redline | 6,500 rpm | E46 330i (non-ZHP) instrument redline and fuel cut |
| Intake manifold | DISA two-stage resonance flap, closed below ≈ 3,750 rpm, open above ≈ 4,100 rpm | Pelican Parts / BimmerFest technical articles |
| VANOS adjustment | intake centreline 74–134° ATDC, exhaust 76–136° BTDC (60° of crank each) | BMW repair instruction 11 31 505 (M52TU/M54/M56), as quoted by forum/blog summaries |
| Engine mass | 130–171 kg depending on source and ancillaries | spec aggregators (130 kg) vs a BMW press figure quoted on BimmerFest (171 kg); no single reliable figure |

These were gathered through web-search results; the development container could not open the pages themselves (its
network policy blocked them), so every published figure above was accepted only where several independent results
agreed. The secondary figures below are enthusiast measurements and are treated as such.

Measured by enthusiasts (secondary sources, used where BMW publishes nothing): 135 mm rods, 22 mm pins, 60/45 mm main/rod
journals and forged-steel crank (E46Fanatics "M54B30 specs and measurements", wersis.net service data), 211 mm deck
height for the M52TU/M54 block (BimmerFest, R3VLimited), ≈ 34 cc chambers and ≈ 0.7 mm gasket (E46Fanatics), 313 g OEM
piston with rings, 602 g rod with bearing, 30.5 mm exhaust valves (E46Fanatics), 68 mm throttle (BimmerWorld listing),
≈ 215 cc/min injectors (Five-O / injector listings), 240°/228° advertised cam durations with 9.7/9.0 mm lift (forum
measurements disagree on the lift point; the advertised event is taken as seat-to-seat), 6.5 L oil.

How each authored value was set:

| Kind | Values |
|---|---|
| Published, used exactly | bore, stroke, cylinders, rod length, pin and journal diameters, deck height, rev limit (tune), cam lifts |
| Derived, never authored | displacement 2979.3 cc and compression 10.20:1 (from chamber 34 cc, gasket 85 mm × 0.7 mm, deck clearance 0.7 mm from a 30.5 mm compression height, and a 12.1 cc bowl solved so the volumes give the published 10.2 — the one fitted geometric value; the forum's 17.4 cc "below the deck" measurement gives 9.9) |
| Converted | 1 mm cam durations 190°/179° from the advertised 240°/228° by the model's own harmonic lift profile (event = 1 mm duration + 50°); VANOS park centrelines 134° (intake, fully retarded) and 136° (exhaust) with 60° of intake phaser |
| Estimated (no source) | port flows (valve curtain area × typical discharge coefficients: 226 CFM at 10 mm intake, 168 CFM exhaust), intake 680 CFM and runner 380 mm (DISA's open stage) with the default 33.6 mm runner diameter, manifolds 700 CFM, exhaust 600 CFM, spring forces (set for float ≈ 7,100 rpm), ratings (rods 32/90 kN, pistons 120 bar, block 150 bar, bearings), oil pump 16 cc/rev at 450 kPa, radiator 1,900 W/K, masses and inertias (dual-mass flywheel 0.13 kg·m²) |
| Calibrated with the dev tools | stock tune: VANOS schedule (`calibrate-cams`), VE (`calibrate-ve`), spark (`calibrate-spark`, RON 98, 1.5° knock margin) — no table was edited by hand to reach the published output |

**DISA as two effective stages** (Intake Gas Dynamics 2.0, Phase 1; the locked procedure is section 4 of
docs/milestones/INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md, the frozen derivation is
docs/milestones/intake-gas-dynamics-2/M54_DISA_DERIVATION.md). `m54.intake.disa`: `runner_length_mm` 469.1 (flap closed,
stage 0), `switched_runner_length_mm` 380 (flap open, stage 1); `ecu.m54_oem` drives it (`intake_runner_control`); the
tune's `intake_runner_switch_rpm` comes from the tune driver (the stage-held full-load torque crossover: 3,900 rpm).

| Value | Used as | Class | Basis |
|---|---|---|---|
| Switching mechanism (vacuum flap, sprung open) | ECU drives DISA (`intake_runner_control: true`) | sourced (secondary, corroborated) | Pelican Parts, BimmerFest, eEuroparts |
| Switch band 3,750 / 4,100 rpm | crossover target 3,925 rpm (centre), check band 3,750–4,100 | sourced (secondary, corroborated) | same |
| Topology: two groups of three, the flap joins them | why a stage is *effective* | sourced qualitatively; layout ambiguous | Pelican Parts, ASC, BMW training wording |
| Open-stage acoustic length | 380 mm | **estimated** (no source) | band 300–450 mm |
| Runner diameter (both stages) | 33.6 mm (0.40 × 84 mm) | **estimated** (A5 default, flagged by the validator) | band 30–40 mm |
| End correction | 0.8216·r | sourced (physics) | Norris & Sheng 1989 |
| V_eff | 302.2 cc | derived | bore, stroke, CR |
| Closed-stage effective length | 469.1 mm (tuned 3,676 rpm; open stage 4,209 rpm) | **derived** (A-D1 + model, frozen before any M54 output) | M54_DISA_DERIVATION.md |
| Tune `intake_runner_switch_rpm` | 3,900 rpm | calibrated (tune driver) | tune manifest |
| Group plenum volumes, resonance tubes, flap bore; firing order | not used | unknown / not needed by Phase 1 | — |

Not modelled: the flap is sprung open, so a real M54 whose ECU cannot drive it runs the open (short) stage; in the model a
variable intake without control stays on its stage 0, here the closed (long) one.

Simplifications (the part descriptions say so where a player would notice): the **exhaust VANOS** is held at its park
position (in this model an exhaust phase would only add overlap, which it treats purely as a filling cost — there is
no exhaust-opening/blowdown term); **DISA** is two effective runner stages of one runner–cylinder mode each (above); the
real manifold's group plenums, resonance tubes and flap are not modelled (the plenum mode is deferred);
**MS43 meters air with a hot-film mass-air-flow meter**, represented here by the model's speed-density ECU with a
calibrated VE table; the E46's **returnless 3.5 bar fuel system** is represented as the model's manifold-referenced
regulator (equivalent to MS43's pressure-compensated injection); the map-controlled thermostat is a fixed 90 °C one;
the **dual-mass flywheel** is one inertia (no torsional isolation). Results against the reference and the acceptance
bands: SIMULATION_SPEC.md, "Second engine family".

Compatibility: every M54 bottom-end and top-end part carries its own interface keys (`m54.deck`, `m54.cam_carrier`,
`m54.intake_flange`, `m54.exhaust_flange`, `m54.sump_flange`, `m54.bellhousing`); K20 head gaskets, oil pumps and
flywheels now require the K20 keys too (they had none, so they would have bolted onto the six). Parts with no
mounting interface stay universal and fit both families: throttles, injector sets (the count must match), fuel pumps,
radiators, ECUs and exhaust systems (`throttle.68mm`, `injectors.6x230cc`, `exhaust.twin_50mm`, `fuel_pump.m54_oem`,
`radiator.m54_oem`, `ecu.m54_oem` are the M54's).

## Synthetic engine matrix (`content/test/engine-matrix/`)
Nine fictional engines that exist to prove the engine architecture (ENGINE_AUTHORING_GUIDE.md §10): SOHC 2-valve I4,
turbo I3 with variable valve lift, turbo I4 with a phaser, I6 with a variable intake, turbo flat-4 with one turbo for
both banks, 60° V6 with a Y-pipe, twin-turbo 60° V6, 90° pushrod V8, 90° DOHC V8 with phasers — with shared parts
(`shared_parts.json`: a basic and a full-function ECU, injector sets, throttles, a Y-pipe) and engine-only and swap
scenarios. Every block provides `m54.bellhousing` so the engines can be swapped into the Isar C30. The layer is test
content: loaded with `--mods content/test` or `CARSIM_MODS_DIR`, never by the base game. Tunes were generated with the
dev calibrators.

## Content strategy
Two engine families: the Kestrel K20 (fictional four, the prototype's engine) and the Isar M54 (a real engine used to
validate that families are data, above). A new family should follow the M54's route: published geometry, estimated
flows and ratings stated as estimates, base maps from the calibration tools, and no simulation code.
