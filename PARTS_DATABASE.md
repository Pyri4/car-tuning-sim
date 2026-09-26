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
follower + ⅓ spring), `valves_per_cylinder`, `material`.

### `camshafts` (intake + exhaust pair)
`intake_duration_deg`, `exhaust_duration_deg` (crank degrees at 1 mm lift), `intake_lift_mm`,
`exhaust_lift_mm`, `lobe_separation_deg` (cam degrees). Overlap is derived.

Cam timing (optional; see SIMULATION_SPEC.md, "Cam timing"): `intake_centerline_deg` (crank degrees ATDC) and
`exhaust_centerline_deg` (crank degrees BTDC) state where the lobes sit as installed — a degreed-in pair, or the
**park** position of cam phasers (intake fully retarded). Both or neither; without them the pair sits straight up
on its lobe separation, as every K20 cam does. `intake_phaser_range_deg` (default 0) is how far an intake phaser can
advance the intake cam from park; it needs the centrelines and an ECU with `cam_phase_control`.

### `valve_springs`
`seat_force_n`, `open_force_n` (force at full lift; sets valve-float speed), `max_lift_mm` (coil bind
/ retainer clearance).

### `intake_manifold`
`runner_length_mm` (tunes the torque peak), `flow_cfm` (includes filter/inlet).

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
`cam_phaser_uncontrolled`).

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
`id`, `name`, `description`, `engine` (engine family id), `drivetrain` (`rwd`|`fwd`), `curb_mass_kg`
and `front_weight_fraction` (factory build), `wheelbase_m`, `track_front_m`, `track_rear_m`,
`cg_height_m`, `yaw_inertia_kg_m2`, `drag_coefficient`, `frontal_area_m2`, `max_steer_deg`,
`bump_travel_mm` (wheel travel from factory ride height to the bump stops, default 75; lowering
takes it away), `slots`, `stock_parts`.

Slot ids are free; the vehicle model finds each part by its role: exactly one required slot of each
of `clutch`, `gearbox`, `differential`, `suspension` and `brakes`, and one required `tires` slot per
axle, marked `"axle": "front"` / `"axle": "rear"` (`axle` is only valid on tyre slots).

## Engine families (`engines`)

| Field | Meaning |
|---|---|
| `id`, `name`, `description` | Identity |
| `cylinders`, `layout` | `cylinders` must match the installed block; `layout` is `inline`, `v` or `flat` (descriptive for now) |
| `slots[]` | `id`, `category`, `display_name`, `required` (default true), `install_after` (slot ids that must be installed first — also defines removal order), `accessible_in_vehicle` |
| `stock_parts` | slot id → part id (factory build) |
| `stock_tune` | tune id (factory calibration) |

The `install_after` graph must be acyclic. A slot can be filled only when all of its
`install_after` slots are filled; it can be emptied only when no filled slot lists it.

The family must fit the engine model (`EngineTopology`), or it fails to load:
- one **required** slot for each of `block`, `main_bearings`, `crankshaft`, `rod_bearings`,
  `connecting_rods`, `pistons`, `head_gasket`, `cylinder_head`, `valve_springs`, `camshafts`,
  `intake_manifold`, `throttle_body`, `injectors`, `fuel_pump`, `exhaust_manifold`, `exhaust`, `oil_pump`,
  `oil_pan`, `radiator`, `flywheel`, `ecu` — the model has no fallback without them;
- at most one slot each for the optional `turbocharger` (empty = naturally aspirated) and
  `intercooler` (empty = none);
- no category the model reads in more than one slot. Per-cylinder and per-bank parts are sold as a
  set in one slot (four pistons; a V engine's pair of heads). Twin turbos, per-bank intake/exhaust,
  dry sumps and superchargers need model work first.

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
position, 0–80, looked up at the MAP the ECU last read), `rev_limit_rpm`, `idle_rpm`, `knock_control_enabled`.

The VE table, displacement, dead time and fuel density are required (saves older than version 4 get the
installed injectors' dead time and their fuel's density on load). Generate a base table for the build the tune is meant
for with `carsim calibrate-ve <engine> [build options]` (it prints the rows to paste); an engine whose
breathing differs from that build runs off its target λ until the table is re-tuned. For a new engine family the
base maps come from three dev tools, in this order: `calibrate-cams` (a phaser's schedule: the advance that traps the
most air at each point), `calibrate-ve`, `calibrate-spark` (`min(best-torque − 1°, knock limit − 1.5°)` on the fuel
given), then `calibrate-ve` once more.

## Scenarios (`scenarios`)
New-game starting points: `id`, `name`, `description`, `engine`, `vehicle` (optional car id; its
`engine` must match the scenario's), `money`, `fuel`, `tune` (empty = the engine's stock tune), `wear`
(engine or chassis slot → 0–1), `fatigue` (slot → {failure_mode: 0–1}), `inventory` (part ids on the
shelf). Failure modes use snake_case names (`detonation`, `head_gasket_breach`, ...). With a vehicle,
the garage holds the car's stock chassis parts alongside the engine; the car can be driven only with
the engine installed in it, every required chassis slot filled, and a runnable, unseized engine.

## Compatibility
Two mechanisms, both validated by `AssemblyValidator`:

1. **Interfaces** (data): a part's `requires` keys must be `provides`d by another installed part.
   Used for mounting patterns (`k20.deck`, `k20.cam_carrier`, turbo flanges, ...).
2. **Physical rules** (code reading spec values, never part ids):
   - set counts equal the cylinder count;
   - crank main journals = block saddles = main bearings; crank pins = rod big ends = rod bearings;
     piston pin = rod small end (±0.01 mm);
   - piston bore = block bore (±0.05 mm);
   - gasket bore ≥ cylinder bore;
   - piston-to-head clearance > 0 (error), ≥ 0.6 mm (else warning); deck clearance ≤ 1.5 mm (else
     knock-prone warning);
   - compression ratio derived from geometry; warnings above 13.5:1 and below 7.5:1;
   - cam lift ≤ spring usable lift (coil bind);
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
| Estimated (no source) | port flows (valve curtain area × typical discharge coefficients: 226 CFM at 10 mm intake, 168 CFM exhaust), intake 680 CFM and runner 380 mm, manifolds 700 CFM, exhaust 600 CFM, spring forces (set for float ≈ 7,100 rpm), ratings (rods 32/90 kN, pistons 120 bar, block 150 bar, bearings), oil pump 16 cc/rev at 450 kPa, radiator 1,900 W/K, masses and inertias (dual-mass flywheel 0.13 kg·m²) |
| Calibrated with the dev tools | stock tune: VANOS schedule (`calibrate-cams`), VE (`calibrate-ve`), spark (`calibrate-spark`, RON 98, 1.5° knock margin) — no table was edited by hand to reach the published output |

Simplifications (the part descriptions say so where a player would notice): the **exhaust VANOS** is held at its park
position (in this model an exhaust phase would only add overlap, which it treats purely as a filling cost — there is
no exhaust-opening/blowdown term); **DISA** is one effective runner length (the model has one runner resonance);
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

## Content strategy
Two engine families: the Kestrel K20 (fictional four, the prototype's engine) and the Isar M54 (a real engine used to
validate that families are data, above). A new family should follow the M54's route: published geometry, estimated
flows and ratings stated as estimates, base maps from the calibration tools, and no simulation code.
