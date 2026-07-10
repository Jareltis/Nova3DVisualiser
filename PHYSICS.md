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

The target is **not** full PhysX — no continuous collision detection (CCD), no reduced-coordinate
articulations, no TGS soft-step. Deliberately a small, correct, stable core. (Bilateral **joint
constraints** — Plan C1 — DO now run, but as maximal-coordinate constraints in the same PGS loop as
contacts, not as a reduced-coordinate articulation solver.)

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

The staged rewrite (Stages 1–7b) is **done**, and the three highest-leverage follow-ons have since shipped as
**Plan C** (see **Current status — Plan C** below): **joints** (C1), **dynamic convex-hull mesh bodies** (C2),
and the **capsule player** (C4). The items still open below are **AGREED but NOT SCHEDULED** — a wish-list,
not a committed roadmap.

**Governing rule (non-negotiable):** every one of these MUST build **ON** the unified impulse solver — the
same warm-started **sequential-impulse / PGS substep loop**, reusing `RigidBody` / `ImpulseWorld` / the
integrator. **Never a parallel or second physics path** — that decoupling is exactly the architecture the
rewrite removed, and re-introducing it would bring back the class of bugs (glue between separate passes) the
whole effort eliminated.

- **Convex DECOMPOSITION for concave dynamic meshes** — a dynamic mesh currently simulates as a **single
  convex hull** of its vertices (Plan C2), so a concave shape collides as its convex envelope. Splitting a
  concave mesh into multiple convex hulls (e.g. V-HACD) — each a `RigidBody` shape feeding the same GJK-EPA
  narrow-phase + `PolyManifold` — would give true concave dynamics. The remaining mesh-physics item.
- **Principal-axis inertia diagonalisation** — `ConvexHull.ComputeMassProperties` returns a **diagonal**
  inertia in the input axes (products of inertia dropped — exact for an axis-symmetric hull, small for the
  near-symmetric game meshes we target). Diagonalising the full covariance to the true principal axes is a
  possible refinement.
- **CCD / speculative contacts** — still a **NON-GOAL** (consistent with the North Star: no CCD). Fast or
  thin bodies at coarse dt can tunnel; the anti-fling rails (`MaxLinSpeed` / `MaxAngSpeed` / `RunawayBound`)
  keep them **bounded**, and the realistic dt range is jitter-free. Revisit only if fast projectiles or thin
  walls actually appear in real use.

## Current status — Plan C (joints + dynamic convex hulls + capsule player)

Three capabilities were added **ON** the completed solver — each a bilateral / narrow-phase extension of the
SAME warm-started PGS substep loop, never a parallel path, each gated by a headless test.

### C1 — Joint constraints (`SampleGame/Physics/Joint.cs`, `JointBuilder.cs`)

Bilateral constraints solved in the **same substep** as contacts. A `Joint` mirrors the contact pipeline's
phases — `Prepare(h)` / `WarmStart()` / `SolveVelocity()` / `SolvePosition(h)` — and `ImpulseWorld` runs every
active joint's hook interleaved with the contact solve (warm-start, then each velocity iteration, then the
split-impulse positional pass). **THE invariant:** a joint impulse is **BILATERAL — never clamped** (it may
push AND pull to hold the constraint), unlike a contact's normal impulse clamped `≥ 0`. When `Joints` is empty
every hook is a no-op, so a joint-free scene steps **bit-identically**.

- **Shared point constraint** (`PointConstraint`) — pins A's `LocalAnchorA` to B's `LocalAnchorB` coincident
  (removes the 3 translational DOF) as **three scalar rows** along the world X/Y/Z axes (PGS recovers the
  coupling); a **split-impulse** positional pass closes the anchor gap, its bias capped at `MaxBiasSpeed`
  (shared with contacts). Reused verbatim by ball-socket AND hinge.
- **Ball-socket** (`BallSocketJoint`) — a thin wrapper over `PointConstraint` (rotation free).
- **Hinge** (`HingeJoint`) — the point pin PLUS **two angular axis-lock rows** (a deterministic tangent basis
  `⟂` the hinge axis, via the contact solver's `TangentBasis`) that remove the 2 relative-rotation DOF off the
  axis, leaving free spin about it. Angular drift is corrected in the positional pass from `err = aA × aB` (the
  two world axes' cross, `≈ sin` of the tilt). Two OPTIONAL axis constraints (both default OFF → a plain hinge
  is bit-identical to a base hinge): a **LIMIT** — a *unilateral* angle stop clamped like a contact normal
  (only pushes θ back inside `[Lower, Upper]`, θ measured from the assembly pose); and a **MOTOR** — a velocity
  drive whose accumulated impulse is clamped to `±(MaxMotorTorque·h)` (a torque cap). Solve order is point →
  axis-lock → **motor → limit last**, so a limit can arrest the motor (a servo).
- **Distance** (`DistanceJoint`) — RIGID (default): a single 1-D velocity row holding the anchor-separation
  rate at 0 + a split-impulse pass toward `RestLength`. SPRING (`SpringEnabled && Frequency > 0`): the
  **Box2D/Catto soft-constraint** formulation — CFM `γ` + a soft mass from the frequency/damping, the position
  error fed back as a bias velocity `C·h·k·γ` in the velocity solve, **NO** positional pass. `jointtest` checks
  the static deflection matches `g/ω²`.
- **World entities + bridge** — a joint is a `JointConfig` (a stable `Id` in the object-id space; `Kind` =
  `ballsocket` / `hinge` / `distance`; `BodyA` / `BodyB` object ids, `-1` = the fixed WORLD anchor at the
  origin; local-frame `AnchorA` / `AnchorB`; hinge `Axis` + limit/motor fields; distance `RestLength` + spring
  fields). The pure `JointBuilder.BuildJoint` translates a config into a live `Joint`. The scene bridge
  (`BuildFrameJoints`) keeps **persistent** live joints (their accumulated-impulse warm-start survives frames),
  rebuilding one only when a referenced body changes.
- **Anchoring contract (F1)** — a joint side resolves to a solver body by kind: an object with **gravity +
  collision** is a **dynamic** body; **any other existing object** (a light or camera marker, a static prop, the
  platform) is a **fixed/kinematic anchor** — an immovable body (zero inverse mass) whose pose is refreshed from
  the live instance every frame, so **moving the anchor object in the editor drags the pinned body** (it wakes
  the sleeping partner, since a static anchor never joins a contact island). Id `-1` is a fixed WORLD point at the
  origin. An anchor uses the same reference frame as the marker (`JointAnchorWorld`) — an `Object3d` at its OBB
  centre oriented by `LocalRotate`, a sphere at its position — so a rotated anchor also rotates the hinge axis. A
  joint with **both sides static** (e.g. platform↔world) is **inactive** (zero effective mass — nothing to move,
  and NaN-safe), and a joint whose side was **deleted** is inactive too. Instead of a silent skip, the bridge
  records a per-joint status (`JointStatusReason`) the editor shows: **"Joint: active"** or **"Joint: inactive —
  &lt;reason&gt;"** (world gravity off / a referenced body was deleted / both sides are static).
- **Joints × sleeping / assembly / collision (F2)** — the solver/bridge integration that makes jointed bodies
  behave in live play:
  - **Sleep understands joints.** A `Joint` exposes `PositionError()` (its current violation magnitude). A
    dynamic body being **dragged by a still-violated joint** (error > `JointSlop` ≈ 0.02) is kept awake (its real
    velocity is pinned to the anchor's ~0 by the velocity solve, but the split-impulse is still closing the gap —
    it is NOT at rest), so it no longer falls asleep in mid-air and "loses gravity". Joints also **union their two
    dynamic ends into one sleep island** (a static end doesn't merge, like a contact), with moving-wakes-sleeping
    propagation. A **satisfied** hanging pendulum at rest **still sleeps**; a later disturbance (a moved anchor,
    or the other island member moving) re-wakes it. `JointActive()` wakes a lone sleeping end under a **static**
    anchor whenever the joint is violated, instead of deactivating it.
  - **Assembly snap (ONCE per joint, F3).** The movable end is **teleported once** so a joint begins SATISFIED —
    no multi-second "rope contraction" creep. It fires exactly ONCE per joint config: when it is **authored**, or
    when a **saved world is loaded** (fresh config identities). It NEVER re-fires on a later runtime rebuild
    (`_snapEvaluated` guards it) — a **gravity toggle** (which recreates the body), body recreation, a
    **retarget**, or an **anchor/kind edit** rebuild the joint but converge DYNAMICALLY (the split-impulse rate,
    kept awake by the sleep integration above) rather than teleporting. This fixed the bug where toggling gravity
    off→on teleported a jointed body into its anchor. Snap convention: exactly one dynamic end → snap it; **both
    dynamic → snap B**. ballsocket/hinge coincide the anchors (translation only); distance (rigid AND spring) move
    along the anchor axis to `RestLength`. Velocities are preserved; the write-back carries it to the instance +
    `_physMoved`. (A joint-param EDIT also invalidates the built `Joint` — its anchors/axis/kind are copied at
    build — so it rebuilds next frame with the fresh values; the snap-evaluated mark persists, so no re-snap.)
  - **Retarget keeps the world point.** Re-pointing `BodyA`/`BodyB` (`RetargetJointBody`) rewrites the local
    anchor so the anchor's **world position is preserved** (the stored anchor number means a world point for `-1`
    but a body-local offset for a real id) — no sideways lunge.
  - **collideConnected — per-joint (F4, default off).** `JointConfig.Collide` (default `false`) controls whether
    a joint's two bodies also collide with each other, keyed into `ImpulseWorld.NoCollide` by `RigidBody.SceneId`.
    **Box2D ShouldCollide semantics:** a real-real pair is excluded from contacts if **any** connecting joint has
    `Collide == false`; a pair whose **every** connecting joint has `Collide == true` collides normally (the
    refill just adds the pair for each `Collide==false` joint). Refilled every frame, so a live `JCollide` toggle
    takes effect next frame. A world-side (`-1`) endpoint contributes no collidable pair. Every non-jointed pair
    is untouched (bit-identical). **Guidance:** enable `Collide` only when the joint's satisfied geometry does NOT
    force overlap — **surface/edge anchors** (real chain links that should bump instead of folding through each
    other, a door vs its post). **Centre anchors + `Collide=on` is a contradictory spec** (the satisfied pose
    overlaps, so the unilateral contact fights the bilateral pin) — leave it off there.
  - **Hinge axis in A's frame.** `JointBuilder` derives the hinge axis in **body A's frame** (world for `-1`) into
    one world axis, then expresses it per-body — so differently-oriented bodies share ONE physical axis and the
    perpendicular locks don't fight. For aligned bodies this is bit-identical to the old verbatim copy.
- **Editor + sync** — a **`joint`** spawn type places a joint entry: a pickable, non-colliding **line marker**
  spanning the two anchors, coloured by kind (**ball-socket cyan, hinge orange, distance green**), a hinge
  adding an **axis stub**; `Field.JointKind` cycles `ballsocket → hinge → distance` and the inspector shows
  kind-dependent fields. Joints ride the world config (`WorldConfig.Joints` — saved / loaded and carried in a
  join's world sync) and live edits stream as `WorldEditPacket` ops **3 (JointModify) / 4 (JointSpawn) /
  5 (JointDelete)** with in-place mutation. Guarded by `jointtest` (solver behaviour) + `editortest`
  (authoring + the net ops).
- **By-design (not bugs):**
  - **Slight overlap is normal.** Resting contacts allow a small `Slop` penetration (the solver removes only
    *excess* penetration), and during a fast swing a jointed **colliding** pair (`Collide=true`) can transiently
    overlap a little deeper: the bilateral pin corrects its violation at the full split-impulse rate while the
    unilateral contact corrects only at the `Beta` rate, so for a frame or two the contact lags. This is standard
    iterative-solver behaviour and self-corrects within a few substeps — a *persistent* deep overlap would be a
    bug, transient overlap is not.
  - **Infeasible distance chains stretch.** An editor-dragged static anchor is **kinematic** — infinitely strong,
    unaffected by the constraints hanging off it. When a boundary condition makes a distance chain **infeasible**
    (e.g. two anchors dragged farther apart than the sum of the rods' rest lengths), the constraint can't be
    satisfied, so the solver **shares the violation** across the rods and they VISIBLY stretch; restoring a
    feasible geometry restores the lengths. `RestLength` is a *target* length, not a breaking strength — a rod
    doesn't snap, it stretches (breakable joints are a future idea).

### C2 — Dynamic convex-hull mesh bodies (`ConvexHull.cs`, `Gjk.cs`, `Contact.cs`)

A gravity+collides **custom mesh** object now simulates as its **true convex hull** instead of a loose OBB.

- **Convex hull** (`ConvexHull.Build`) — **quickhull** over the mesh vertices: duplicates welded onto a grid, a
  single extent-scaled epsilon, an initial simplex + horizon iteration, **outward winding** enforced, the loop
  defensively iteration-capped; **null** on degenerate input (< 4 non-coplanar / coincident / collinear /
  coplanar), whereupon the bridge falls back to a box AABB. **Mass properties** (`ComputeMassProperties`) —
  closed-polyhedron signed-tetrahedron decomposition → volume, COM, and a **DIAGONAL** inertia (products of
  inertia **dropped** — a standard first approximation, EXACT for an axis-symmetric hull: a unit cube hull
  reproduces `PhysicsMath.BoxInertia`, verified in `hulltest`). **Polygonal `Faces`** — coplanar adjacent
  triangles merged (union-find) into convex CCW loops, for manifold clipping.
- **GJK + EPA** (`Gjk.cs`) — over a generic **support-function delegate** (works for hull / box / sphere).
  `Distance` returns witness points via the terminating simplex's **barycentric weights**; `Intersect` is the
  boolean; `Penetration` is **EPA** (grows the terminating simplex to a tetra, then a quickhull-style horizon
  expansion) with a documented normal sign (points B→A: translating B by `−depth·normal` separates). Guarded
  by `gjktest`.
- **Contact manifolds** (`Contact.cs`) — a shared **`PolyManifold`** core: the GJK/EPA contact normal picks a
  **reference** face (the candidate most parallel to the normal) and the OTHER shape's **incident** face, then
  **Sutherland–Hodgman** clips the incident polygon against the reference's side planes → **≤ 4** contact
  points, each carrying a **STABLE `Feature` id** so warm-start catches (jitter-free rest / stack). Pairs:
  `HullVsSphere` (a single analytic point), `HullVsBox`, `HullVsHull` — each transform-based, so the non-hull
  partner may be **static or dynamic** (the solver mass-weights both). **`HullVsMesh`** (C2-4) mirrors
  `BoxVsMesh` but queries the **hull's own vertices** against the static mesh's triangle faces (per-vertex
  deepest-penetrating face, the hull-vertex index as the stable Feature id); a high-poly mesh falls back to the
  mesh's box view via `HullVsBox`. Guarded by `hullphystest`.
- **Scene bridge** (`PriviewNetworkScene.Physics.cs`) — an `Object3d` whose descriptor `Type == "mesh"` that is
  gravity+collides builds a `DynamicHull` from its **`LocalVertices · Scale`**; `RigidBody.HullLocalCom` (the
  hull's COM in that scaled-local frame) both **places** the body (`Position = HullLocalCom·rot + objectPos`)
  and is **backed out** on sync-back, so the mesh doesn't drift by its COM offset. The hull is built **ONCE**
  (quickhull is expensive) and rebuilt **only on a scale edit**; velocity / sleep are preserved. Box-like
  **primitives** (cube / ramp / pyramid / …) still simulate as their **OBB** (a cube's OBB equals its hull, and
  box-box is cheaper). It rides the existing per-object physics-sync unchanged. Guarded by `editortest`'s C2-5
  bridge check.
- **Remaining limit** — a mesh collides as a **single convex hull**, so a concave shape uses its convex
  envelope; convex decomposition is a future item (above).

### C4 — Capsule player (`PriviewNetworkScene.Player.cs`)

The walk-mode player collider is now a **vertical capsule** instead of a single sphere: a central segment of
half-length `CapsuleHalf` (0.55) plus radius `CameraRadius` (0.35) — total height ≈ 1.8 (human scale),
`Position` staying the capsule CENTRE so the camera rig is unchanged. Resolution **reuses the existing sphere
resolvers**: for each collider, clamp its height into the capsule segment (a Sphere's centre / an OBB or AABB's
mid-height) → run the SAME `ResolveSphereVsSphere` / `Aabb` / `Obb` at that segment point → translate the body
by the delta. With `CapsuleHalf == 0` it reduces EXACTLY to the old sphere bubble. Ground detection is
unchanged, now read from the **foot cap** (rest at `floorTop + CapsuleHalf + CameraRadius`). So the player has
real body height — blocked by head- and foot-height obstacles a centre-sphere would miss, can't slip under low
overhangs. Guarded by `editortest`'s C4 capsule checks.

## Prior status — Stage 7b (COMPLETE — single engine; Plan C built on top, above)

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
- **Superseded by Plan C:** at Stage 7b a DYNAMIC mesh that is not a box/sphere was simulated as its OBB;
  Plan C2 (above) now simulates a gravity+collides mesh as its true convex **HULL** (a concave mesh still
  uses its convex envelope — see **Convex DECOMPOSITION** under Future directions).

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
