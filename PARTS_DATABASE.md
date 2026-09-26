# Parts Database

## Purpose
The canonical data model for mechanical content. This document describes what the loader in
`src/CarSim.Core/Content/ContentLoader.cs` actually accepts. If the code and this file disagree,
the code is right and this file must be fixed.

## Files and loading
- Content lives under `content/<root>/` (base game: `content/base/`). Every `*.json` file below the
  root is loaded, in ordinal path order.
- A file is an object with any of these arrays: `parts`, `engines`, `fuels`, `tunes`.
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

### Not yet implemented
`turbocharger`, `intercooler` (category ids reserved; schema will be added with the turbo model).

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
