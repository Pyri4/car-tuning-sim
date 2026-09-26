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
- Ids must be unique across the whole root.
- Run `dotnet run --project tools/CarSim.Cli -- validate` to check content.

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

### `valve_springs`
`seat_force_n`, `open_force_n` (force at full lift; sets valve-float speed), `max_lift_mm` (coil bind
/ retainer clearance).

### `intake_manifold`
`runner_length_mm` (tunes the torque peak), `flow_cfm` (includes filter/inlet).

### `throttle_body`
`bore_mm`, `flow_cfm` (wide open).

### `injectors` (set)
`count`, `flow_cc_min` (static flow per injector at `rated_pressure_kpa`, default 300),
`max_duty` (recommended maximum, default 0.85).

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
`knock_control`, `max_rev_limit_rpm`, `tables_editable`.

### `turbocharger`
`compressor_wheel_diameter_mm` (exducer; tip speed → pressure ratio), `compressor_choke_flow_kg_s`
(corrected), `compressor_peak_efficiency`, `compressor_peak_efficiency_flow_kg_s`,
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
- `clutch`: `max_torque_nm`.
- `gearbox`: `ratios` (forward, first gear first), `reverse_ratio`, `efficiency`, `max_torque_nm`,
  `shift_time_s`, `input_inertia_kg_m2`.
- `differential`: `final_drive_ratio`, `type` (`open` | `clutch_lsd` | `locked`), `preload_nm`,
  `locking_accel`, `locking_decel` (0–1), `max_torque_nm`.
- `tires` (a pair, one axle): `width_mm`, `aspect_ratio`, `rim_diameter_in`, `peak_friction`,
  `load_sensitivity`, `peak_slip_ratio`, `peak_slip_angle_deg`, `rolling_resistance`, `inertia_kg_m2`
  (per wheel), `compound`.
- `suspension`: `front_spring_n_mm`, `rear_spring_n_mm` (wheel rates), `front_damper_ns_m`,
  `rear_damper_ns_m` (per wheel), `front_arb_nm_deg`, `rear_arb_nm_deg`, `ride_height_offset_mm`.
- `brakes`: `front_max_torque_nm`, `rear_max_torque_nm` (per wheel at full pedal).

## Vehicles (`vehicles`)
`id`, `name`, `description`, `engine` (engine family id), `drivetrain` (`rwd`|`fwd`), `curb_mass_kg`
and `front_weight_fraction` (factory build), `wheelbase_m`, `track_front_m`, `track_rear_m`,
`cg_height_m`, `yaw_inertia_kg_m2`, `drag_coefficient`, `frontal_area_m2`, `max_steer_deg`,
`slots` (must include `clutch`, `gearbox`, `differential`, `tires_front`, `tires_rear`, `suspension`,
`brakes`), `stock_parts`.

## Engine families (`engines`)

| Field | Meaning |
|---|---|
| `id`, `name`, `description` | Identity |
| `cylinders`, `layout` | Must match the installed block |
| `slots[]` | `id`, `category`, `display_name`, `required` (default true), `install_after` (slot ids that must be installed first — also defines removal order), `accessible_in_vehicle` |
| `stock_parts` | slot id → part id (factory build) |
| `stock_tune` | tune id (factory calibration) |

The `install_after` graph must be acyclic. A slot can be filled only when all of its
`install_after` slots are filled; it can be emptied only when no filled slot lists it.

## Fuels (`fuels`)
`id`, `name`, `octane_ron`, `stoichiometric_afr`, `lower_heating_value_mj_kg`, `density_kg_l`,
`charge_cooling_factor` (evaporative cooling relative to gasoline = 1.0), `description`.

## Tunes (`tunes`)
See SIMULATION_SPEC.md (ECU section) — `rpm_axis`, `load_axis_kpa`, `target_lambda[load][rpm]`,
`ignition_advance_deg[load][rpm]`, optional `boost_target_kpa[rpm]`, `rev_limit_rpm`, `idle_rpm`,
`knock_control_enabled`.

## Scenarios (`scenarios`)
New-game starting points: `id`, `name`, `description`, `engine`, `money`, `fuel`, `tune` (empty = the
engine's stock tune), `wear` (slot → 0–1), `fatigue` (slot → {failure_mode: 0–1}), `inventory`
(part ids on the shelf). Failure modes use snake_case names (`detonation`, `head_gasket_breach`, ...).

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

## Content strategy
One coherent engine family first (Kestrel K20). Expand only after the schema and tooling are stable.
