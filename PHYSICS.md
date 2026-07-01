# Physics direction — the rigid-body constraint-solver rewrite

This file is the **permanent, tracked record** of where the physics is going and why. It is
committed on purpose (unlike `CLAUDE.md` / `backlog.md`, which are git-ignored working notes).

## North star

A **unified impulse-based rigid-body constraint solver** (sequential impulse / projected
Gauss–Seidel over a contact manifold, warm-started, with restitution + friction + positional
correction + sleeping) giving **Box2D / simple-3D-engine class SOLIDITY**:

- stable resting and stable **stacks** (no jitter),
- believable **tumbling / rolling / sliding** as *emergent* behaviour of the solver,
- **no energy injection, no fling, no sink-through.**

The target is **not** full PhysX — no continuous collision detection (CCD), no articulations,
no TGS soft-step. Deliberately a small, correct, stable core.

**Governing rule:** *correctness + stability over features.* A shortcut that conflicts with
stable, energy-conserving contact is a **BUG, not a simplification** — fix it, don't keep it.

## Why (the problem with the current/legacy layer)

The legacy dynamics are a set of **decoupled special-case passes** — `StepFallY`,
`StepMeshGravity`, `StepHorizontalPhysics`, `StepSphereGravity`, support-edge tipping — where
**rotation and translation are computed separately and glued together**. Realistic resting,
stacking, tumbling, rolling and face-alignment are **emergent properties of a single unified
solver** and cannot be produced reliably by decoupled passes (the tipping/tumbling stages hit
exactly this ceiling: glue works for one case and fights the next). We are replacing that
architecture with a real solver — **incrementally**, each stage gated by a headless test.

## Acceptance criteria (the standing bar — applies to EVERY stage)

- **No penetration** beyond a small slop at rest.
- **No energy injection:** velocity / rebound height never grows; restitution decays to rest.
- **Stable rest:** bodies settle (`|v|,|ω| → 0`) and **STAY** (no jitter), then **sleep**.
- **Bounded + frame-rate independent:** nothing approaches `RunawayBound`; a dt sweep
  (1/60 … 1/1) gives consistent results.
- **Every stage ships a headless stability test** asserting the above.

## Migration strategy — no big-bang rewrite

The new solver is a **self-contained module** (`SampleGame/Physics/`), selected at runtime by a
new world field:

The rewrite is **COMPLETE (Stage 7b)**: there is now a **single** object-dynamics engine — the impulse
solver in `SampleGame/Physics/` — with **no engine switch and no legacy passes left**. `PhysicsConfig`
no longer has an `Engine` field; a world JSON that still carries a stale `"engine"` key loads fine
(`System.Text.Json` ignores the unmapped member). The staging above was the migration path: each stage
shipped behind the (now removed) `Engine` switch and was gated by a headless test before the cutover, so
the running app never broke.

## Reuse — do NOT rewrite geometry/math

The rewrite replaces the **integration + contact-resolution architecture only**. It **reuses**
the existing, tested geometry/math:

- `PriviewNetworkScene.Quat` + `IntegrateQuat` (drift-free SO(3) orientation integration),
- `BoxInertia` + the world-inertia rotation `R·I⁻¹·Rᵀ`,
- OBB / sphere / `ClosestPointOnTriangle` / the sphere-vs-mesh closest-feature code,
- `SatBox3D`, the BVH.

## Staged plan

- **Stage 1 — solver skeleton + sphere-vs-static.** `RigidBody`, fixed-substep integrator,
  sequential-impulse **normal** constraint with **split-impulse positional correction** +
  restitution, **warm-start**, **sleeping**; contact generation for a dynamic **sphere** vs
  **static** box / mesh / sphere. Headless `impulsetest`. **✓ done.**
- **Stage 2 — Coulomb friction** (clamped by the normal impulse) + **box-vs-static** (a box
  rests flat on the ground, no slide, no jitter). **✓ done.**
- **Stage 3 — box-vs-box manifold** (SAT + face clipping, multiple contact points) → stable
  **stacks**. **✓ done.**
- **Stage 4 — general dynamic-vs-dynamic** primitive pairs (sphere-box, sphere-sphere), mass-
  weighted. **✓ done.**
- **Stage 5 — sphere/box vs REAL triangle mesh** (ramps, wedges, pyramids) via closest-feature
  contact — the ramp/tumble case, now on a solid solver. **✓ done.**
- **Stage 6 — rolling / rolling-friction**, resting refinement, sleeping **islands**. **✓ done.**
- **Stage 7a — CUTOVER:** make `impulse` the DEFAULT and wire the new `RigidBody` state (position +
  orientation + linear + angular velocity) to `PhysicsSyncPacket` for multiplayer. **✓ done.**
- **Stage 7b — RETIRE the legacy passes:** deleted `StepFallY`/`StepMeshGravity`/`StepHorizontalPhysics`/
  `StepSphereGravity`/`StepPhysicsOnce` + the tipping (`TipSupport`/`TipAngularAccel`/`AngularAccelT`/
  `AngularImpulse[T]`/`_over`) + the legacy horizontal helpers (`ResolveAabbHorizontal`/`SatRect2D`/
  `XZOverlap`/`NormalImpulse`/`StepFriction`/`BodyAxes`/`LocalBoxSize`/`Box3D`) + the legacy contact
  helpers (`StepFallY`/`ReflectVelocity`/`SphereContactResponse`/the scene `SphereVsMesh`/`SphereVsBox`)
  + the legacy per-object state dicts (`_fallVel`/`_horizVel`/`_angVel`/`_orient`/`_tipCalm`/`_over`) +
  the `PhysicsConfig.Engine` switch. **✓ done — the rewrite is COMPLETE: one solver, no dead code.**

## Future directions (beyond the completed rewrite)

The staged rewrite (Stages 1–7b) is **done**. The items below are **AGREED but NOT SCHEDULED** — recorded so
the direction is captured; this is a wish-list, not a committed roadmap.

**Governing rule (non-negotiable):** every one of these MUST build **ON** the unified impulse solver — the
same warm-started **sequential-impulse / PGS substep loop**, reusing `RigidBody` / `ImpulseWorld` / the
integrator. **Never a parallel or second physics path** — that decoupling is exactly the architecture the
rewrite removed, and re-introducing it would bring back the class of bugs (glue between separate passes) the
whole effort eliminated.

- **Constraints / joints** (hinge, ball-socket, spring/distance, motor) — bilateral constraints solved in the
  **SAME PGS loop** as the contact constraints (warm-started; split-impulse for the positional part), just
  with a two-sided impulse clamp instead of the contact's `≥ 0` clamp. Unlocks **ragdolls, doors, chains,
  vehicles, articulated contraptions.** The highest-leverage next capability.
- **Dynamic real-mesh rigid body** — a dynamic mesh that is not a box/sphere is currently boxed to its OBB by
  the solver (a known limit, not a legacy regression — legacy's mesh dynamics was the source of the original
  ramp-teleport). A true dynamic triangle-mesh body (convex decomposition into convex hulls + a GJK-EPA
  narrow-phase feeding the same manifold/solver) is a possible later stage.
- **CCD / speculative contacts** — a **NON-GOAL for now** (consistent with the North Star: no CCD). Fast or
  thin bodies at coarse dt can tunnel; the anti-fling rails (`MaxLinSpeed` / `MaxAngSpeed` / `RunawayBound`)
  keep them **bounded**, and the realistic dt range is jitter-free. Revisit only if fast projectiles or thin
  walls actually appear in real use.
- **Player-collision upgrade** — the walk-mode camera bubble is still **sphere-vs-AABB/OBB**
  (`ResolveSphereVsAabb`/`Obb`), cruder than the object solver. A capsule / convex-hull / per-triangle player
  collision is a polish item. (This is the *player's own* collision, separate from object dynamics — an
  upgrade here doesn't touch the falling-object engine.)

## Current status — Stage 7b (COMPLETE — single engine; future directions recorded above)

The rewrite is **done**: the impulse solver is the **only** object-dynamics engine, the legacy decoupled
passes are **deleted**, and there is **no engine switch**:

- **Legacy retired** — all of `StepFallY`/`StepMeshGravity`/`StepHorizontalPhysics`/`StepSphereGravity`/
  `StepPhysicsOnce`, the support-edge tipping, the legacy horizontal/contact helpers, and the legacy
  per-object state dicts are gone; `StepPhysics` calls the impulse solver directly. `PhysicsConfig.Engine`
  is removed; a stale `"engine"` key in an old world JSON loads gracefully (guarded by `cutovertest`'s
  `stale-engine-key-loads`). No dead/unreferenced legacy code remains (build is 0 warnings / 0 errors).
- **KEPT (shared, still used):** the drift-free `Quat` math (`IntegrateQuat`/`QuatFromEuler`/`EulerFromQuat`/
  `QuatMul`), `BoxInertia` + the inverse-inertia world rotation, `ClosestPointOnTriangle`/`SatBox3D` (the
  impulse box-box manifold's normal), `CombineRestitution`, the player walk-mode collision bubble
  (`ResolveSphereVsAabb`/`Obb`/`Sphere`), and the networking (`PhysicsSyncPacket` + `StepInterpolate`/
  `LerpAngle`). `physicstest` now covers only that shared pure math; `impulsetest` is THE dynamics test.
- **Multiplayer sync** (from Stage 7a) — `PhysicsSyncPacket` carries each authority-moved body's
  **full RigidBody state**: Position + Euler orientation + **full LinVel (X/Y/Z, not just VelY)** + AngVel.
  The authority streams it every `PhysicsSyncEvery` frames (`FlushPhysicsSync` reads LinVel/AngVel from the
  `RigidBody`); the client **dead-reckons** the pose
  (`StepInterpolate` advances Position by LinVel·dt in ALL axes; the orientation by AngVel·dt with
  shortest-arc `LerpAngle`) and eases toward each batch — so a ball/box **falling, rolling and tumbling**
  on the authority is reproduced SMOOTHLY on peers, not frozen between batches. The new `_impBodies` state
  is pruned in `RemoveEntryAt` / cleared in `ApplyReceivedWorld` alongside the other physics-sync state.
  Guarded by `cutovertest`'s `physics-sync-roundtrip` (client Position + Orientation track the authority
  within ~0.4 during a tumble and converge to ~0 at rest, at 20 Hz and 10 Hz batch rates).
- **Preserved (untouched):** the camera/player walk-mode collision bubble (`ResolveSphereVsAabb`/`Obb`),
  non-colliding / gravity-off objects, the editor, graphics toggles, world save/load.
- **Known limit (not a regression):** a DYNAMIC mesh that is not a box/sphere is simulated as its OBB (see
  the "dynamic real-mesh rigid body" item under **Future directions** above).

## Prior status — Stage 6

`SampleGame/Physics/` (`RigidBody`, `Contact`, `ImpulseWorld`) has **rolling friction + sleeping islands**
(Stage 6), so rolling/tumbling bodies come to rest and piles settle and sleep as one group:

- **Rolling friction** — after the velocity solve, each body in contact gets a bounded resistance to SPIN
  scaled by the normal impulse it carried (`Σ combinedRoll·normalImpulse`, the geometric-mean combine of the
  per-object coefficients). It removes angular momentum opposing ω, CLAMPED to `|L|` so it can never reverse
  the spin or add energy (`ω ·= 1 − drop/|L|`). It's SKIPPED for a rotationally-calm body (`|ω| < SleepAng`)
  so a resting stacked box isn't perturbed into phantom drift. The coefficient is small by default (0.05), so
  gravity still wins on a slope (a ball rolls DOWN) and a box on a steep ramp still TUMBLES — it only damps.
  Per-object `RollingFriction` rides the pipeline (`WorldObject`/`GameObject`, `ApplyToInstance`/`FromInstance`,
  `CompareWorlds`, `worldsynctest`, editor `Field.RollingFriction`).
- **Sleeping islands** — bodies connected by contacts are grouped by a **union-find** over the dynamic-vs-
  dynamic contacts; an island sleeps only when ALL its members are calm (transitive, replacing the Stage-3
  direct-neighbour coupling), and a moving body still wakes the sleeping neighbours it touches. So a pile
  (boxes + a settled ball) sleeps cleanly as one group — and the ball, now stopped by rolling friction, lets
  the whole scatter scene sleep (the fix to Stage 4's "doesn't fully sleep").
- **Still OUT of scope:** DYNAMIC mesh vs anything (still boxed to its OBB); retiring the legacy passes /
  making `impulse` the default / wiring the new `RigidBody` state to `PhysicsSyncPacket` for multiplayer —
  that is **Stage 7 (next)**. `legacy` (default) is untouched.

Verified by `impulsetest`: all Stage-1..5 assertions plus **ball-rolling-stops** (a sphere given horizontal
velocity rolls, decelerates, and comes to rest + SLEEPS within a bounded ~6-unit distance — not perpetual —
without reversing or gaining energy), **ball-scatters-stack-sleeps** (the whole scatter scene now sleeps as
one island, ~1.6 s), and **slope-still-rolls / steep-still-tumbles** (rolling friction does not freeze a ball
on a gentle slope or a box tumbling down a steep ramp).

## Prior status — Stage 5

`SampleGame/Physics/` also does **dynamic sphere/box vs static REAL TRIANGLE MESH** (Stage 5) — a box rests
FLAT on a slope, TUMBLES over its edge, and a ball rolls on the true face, all with NO teleport, EMERGENT:

- **Sphere vs static mesh** — the Stage-1 `SphereVsMesh` (closest point over the triangles via
  `ClosestPointOnTriangle`, `centre−closest` normal, winding-independent) already handles this; a ball
  rolls down the real ramp/pyramid face with no teleport. Above `MaxFaces` it falls back to the mesh AABB.
- **Box (OBB) vs static mesh — `ContactGen.BoxVsMesh`** — a pragmatic **corner-sampling manifold** (NOT a
  full box-vs-triangle SAT): each of the box's 8 world corners that penetrates the mesh emits one contact
  AT the corner, with the penetrated triangle's FACE normal (oriented toward the box, so winding doesn't
  matter) and the depth below that face; a cheap per-triangle AABB cull, the corner must project INSIDE
  the triangle (a face contact), a `MaxCornerPen` guard rejects tunnelled corners, and above `MaxFaces`
  it falls back to the mesh's OBB box view. Up to 4 simultaneous corner contacts on a face let the
  sequential-impulse solver **ALIGN the box to the slope** (it rests flat, bottom-face parallel to the
  face) and **TUMBLE it over its edge** when the CoM leaves the support — entirely emergent, no legacy tip.
- **Runtime** — under `Engine=="impulse"`, dynamic box vs static **Mesh** now routes to `BoxVsMesh` (real
  triangles) instead of the OBB box view; the platform (a flat 2-triangle mesh) still rests boxes flat. The
  legacy `StepMeshGravity` tip/slide/`_over` path **never runs in impulse mode** (the `StepPhysics` early
  return to `StepImpulse` excludes the whole legacy pass), so the two tumbling systems never both act on a
  body — the original ramp-teleport bug's root cause.
- **Still inert (later stages):** DYNAMIC mesh vs anything (a dynamic mesh is still boxed to its OBB in the
  runtime — Stage 5 covers STATIC meshes only); **rolling friction** & sleeping **islands** (Stage 6 — a
  struck/rolling ball still rolls on without slowing, as seen in `sphere-on-ramp`); **network sync** of the
  new solver (Stage 7). `legacy` (default) is untouched.

Verified by `impulsetest`: all Stage-1/2/3/4 assertions plus **sphere-on-ramp** (rolls down the real face,
no teleport — `maxStep` tiny), **box-on-ramp-rest** (settles flat ALIGNED to the face — `tiltVsFace`≈0 —
high μ sticks, low μ slides bounded), **box-tumbles-ramp** (a box off-balance on a steep ramp tumbles over
its edge — tilt 1.17→3.13 — moves downhill, no teleport, settles), **box/sphere-on-pyramid** (rest on the
REAL face, well below the bbox top, no teleport), and **box-on-flat-mesh** (rests flat/still on the flat
platform mesh, sleeps).

## Prior status — Stage 4

`SampleGame/Physics/` also does **general dynamic-vs-dynamic PRIMITIVE contact** (Stage 4) — so among
primitives, everything collides with everything:

- **New pairs, same solver:** dynamic **sphere-vs-dynamic-box** (closest point on the OBB, single
  analytic contact) and dynamic **sphere-vs-dynamic-sphere** (centre-line, single analytic contact)
  reuse the existing geometry (`ClosestPointOnObb`, `SphereVsSphere`), now with BOTH bodies dynamic.
  They run through the SAME sequential-impulse solver as box-box: mass-weighted normal + friction
  impulses to both bodies, warm-started, restitution combined per contact, split-impulse correction.
  No new solver code — only new contact GENERATION, dispatched per shape pair (box-box → clipped
  manifold; anything with a sphere → single point) with a deterministic A/B assignment so warm-start
  stays stable. Sleep coupling (Stage 3) now spans all dynamic pairs.
- **Anti-fling rails** (mirroring the legacy engine, now in the solver): the split-impulse position
  bias is clamped (`MaxBiasSpeed`), linear/angular velocity are clamped (`MaxLinSpeed`/`MaxAngSpeed`),
  and a `RunawayBound` backstop zeroes the velocity of any body that escapes — so nothing flings to
  huge coordinates even at coarse dt. (No CCD is still a non-goal: at ~1 FPS a fast body can tunnel a
  thin obstacle; the backstop keeps it BOUNDED, and the realistic dt sweep 1/60…1/20 is jitter-free.)
- **Still inert (later stages):** any dynamic pair involving a **real triangle mesh** (Stage 5 —
  ramps / wedges / pyramids / tumbling; a dynamic mesh is still boxed to its OBB in the runtime),
  **rolling friction** & sleeping **islands** (Stage 6 — a struck ball rolls on without slowing),
  and **network sync** of the new solver (Stage 7). `legacy` (default) is untouched.

Verified by `impulsetest`: all Stage-1/2/3 assertions plus **sphere-on-box** (a dynamic ball rests on
a dynamic box, penetration ≈ 0.005 ≤ slop, sleeps), **sphere-sphere-momentum** (a light sphere moves a
heavy one less than an equal-mass one; momentum conserved, no energy gain), and **ball-scatters-stack**
(a fast ball fired into a 3-box stack disturbs it with NO horizontal-momentum injection and NO energy
injection, everything BOUNDED, the stack settles to rest — the ball itself may roll on, Stage 6).

## Prior status — Stage 3

`SampleGame/Physics/` (`RigidBody`, `Contact`, `ImpulseWorld`) also does **dynamic box-vs-box
contact → stable stacks** (Stage 3):

- **Dynamic OBB vs dynamic OBB** — the same `SatBox3D` + Sutherland–Hodgman face-clipping manifold
  as box-vs-static, but **both** bodies are dynamic: each contact point applies mass-weighted
  linear + angular impulses (`R·I⁻¹·Rᵀ`) to **both** bodies, with per-point friction, all
  warm-started. Pairs are ordered by body Id so the reference/incident choice + feature ids are
  stable frame-to-frame (warm-start catches → no jitter). Positional correction stays split-impulse.
- **Stack stability** — warm-start per persistent feature + **10 velocity / 8 position iterations**
  keep a tall stack from sagging or leaning; a **sleeping body acts as an immovable anchor** in the
  solve (an awake box rests on a sleeping one exactly as on the static ground); and **minimal sleep
  coupling** (a body sleeps only when it AND every dynamic contact neighbour are calm; a moving body
  wakes the sleeping ones it touches) stops a stack from half-sleeping and leaning. Full sleeping
  **islands** are Stage 6.
- **Still inert (later stages):** sphere-vs-dynamic and sphere-vs-sphere dynamic pairs (Stage 4);
  real-triangle mesh / ramps / tumbling (Stage 5); rolling friction / sleeping islands (Stage 6);
  network sync of the new solver (Stage 7). `legacy` (default) is untouched.

Verified by `impulsetest`: all Stage-1/2 assertions plus **stack-stability** (4 aligned boxes stay
upright — maxTilt≈0.010, top-box drift≈0.02, settled inter-box penetration≈0.004 ≤ slop — settle and
sleep, across a dt sweep; the coarse 1-FPS case only stays bounded), **box-on-box-rest** (a box on a
box rests flat/still, pen≈0.002, sleeps), and **box-box-momentum** (a light box shoves a heavy one
LESS than an equal-mass one, momentum conserved, no energy gain).

## Prior status — Stage 2

`SampleGame/Physics/` (`RigidBody`, `Contact`, `ImpulseWorld`) implements the solver skeleton
(Stage 1) **plus Coulomb friction and dynamic-box-vs-static contact** (Stage 2):

- **Friction** — every contact (spheres included) solves two Coulomb-clamped tangent constraints
  after the normal impulse: the accumulated 2D tangent-impulse magnitude is clamped to
  `μ · (accumulated normal impulse)`, so friction never exceeds `μN` and never adds energy. The
  tangent basis is deterministic in the normal and the tangent impulses are warm-started, so a
  resting body neither creeps nor jitters. Per-object `Friction` (μ, default 0.5) rides the usual
  pipeline (`WorldObject`/`GameObject` + `ApplyToInstance`/`FromInstance` + `CompareWorlds` +
  `worldsynctest` + editor `Field.Friction`); two objects' μ combine as the geometric mean
  (matching `CombineRestitution`).
- **Box vs static — contact manifold** — a dynamic OBB vs a static box/box-view generates a
  MULTI-POINT manifold by reference/incident **face clipping** (SAT normal from `SatBox3D`, then
  Sutherland–Hodgman clip → up to 4 points), solved together (normal + friction per point,
  warm-started per feature) so a box rests **flat and still**. Box-vs-static-sphere is a single
  closest-feature point. Positional correction stays **split-impulse** (no energy injection).
- **Runtime** — under `PhysicsConfig.Engine == "impulse"` the scene now simulates dynamic
  **spheres AND boxes** against **static collidables**. Any collidable dynamic `Object3d` is
  simulated as its **OBB** (dynamic meshes are boxed — real-triangle dynamic-mesh contact is
  Stage 5); the static platform/support is a solid **box view** of its world AABB (floored to a
  minimum downward thickness so a zero-thickness platform quad is still a solid support), while a
  dynamic **sphere** still uses the support's **real triangles**. **Dynamic-vs-dynamic pairs are
  still INERT** (only dynamic-vs-static generates contacts — Stages 3–4). `legacy` (default) is
  untouched.

Verified by `impulsetest`: the Stage-1 sphere assertions still hold, plus **box-rest** (a box
dropped flat settles level with no jitter/drift, penetration ≤ slop, sleeps — across a dt sweep),
**box-friction-incline** (high μ sticks, low μ slides with a bounded speed), and
**friction-no-creep** (a box and a sphere at rest do not drift).
