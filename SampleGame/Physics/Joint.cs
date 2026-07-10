using System;
using Nova3DVisualiser;

namespace SampleGame.Physics;

// ---- Plan C1 joint constraints, solved in the SAME warm-started PGS loop as contacts (PHYSICS.md). ----
// A Joint mirrors the contact pipeline's phases (Prepare / WarmStart / SolveVelocity / SolvePosition) so each
// kind (ball-socket C1-1, hinge C1-2, and later limits / motor / spring) is a subclass. THE key difference from
// a contact: a joint constraint is BILATERAL — its accumulated impulse is NEVER clamped (it may push AND pull to
// hold the constraint), unlike a contact's normal impulse which is clamped >= 0 (unilateral, push-only). Do NOT
// copy the >= 0 clamp onto a joint. When ImpulseWorld.Joints is empty every joint hook is a no-op, so a joint-
// free scene steps bit-identically to before.
public abstract class Joint
{
    public RigidBody A = null!, B = null!;
    public abstract void Prepare(float h);        // recompute world anchors / lever arms / axes + effective masses
    public abstract void WarmStart();             // re-apply the impulse accumulated the previous substep
    public abstract void SolveVelocity();         // one PGS velocity iteration
    public abstract void SolvePosition(float h);  // one split-impulse positional iteration
}

// Shared joint helpers. A SLEEPING or STATIC body is an immovable anchor: it contributes 0 to any effective mass
// and is unaffected by an applied impulse — mirroring the contact solver (InvMassOf / DotAngular / the
// ApplyImpulse guard), so a joint under one sleeping end holds the awake end against a frozen anchor without
// corrupting the sleeper.
internal static class JointMath
{
    public static bool Movable(RigidBody b) => b.InvMass > 0f && !b.Sleeping;
    public static float InvMassOf(RigidBody b) => Movable(b) ? b.InvMass : 0f;
    public static Vector3 InvInertiaMul(RigidBody b, Vector3 v)
        => Movable(b) ? ImpulseMath.MulInvInertiaWorld(b, v) : Vector3.Zero;   // I⁻¹_world · v (0 for a fixed body)
}

// The point-to-point (ball-socket) constraint: pins A's LocalAnchorA to B's LocalAnchorB, keeping those two
// world anchor points COINCIDENT — removing the 3 relative translational DOF. Solved as three scalar constraints
// along the world X/Y/Z axes (PGS recovers the coupling), bilateral (unclamped). This is the SHARED core reused
// verbatim by BallSocketJoint AND HingeJoint (the hinge adds angular axis-lock constraints on top).
internal sealed class PointConstraint
{
    public Vector3 LocalAnchorA;      // anchor in A's local (body) frame
    public Vector3 LocalAnchorB;      // anchor in B's local (body) frame

    // ---- per-substep scratch ----
    private Vector3 _rA, _rB;         // world lever arms: R·LocalAnchor (the anchor relative to the body centre)
    private Vector3 _accImpulse;      // accumulated CONSTRAINT impulse (bilateral, NEVER clamped) — persists across substeps for warm-start
    private Vector3 _mEffDiag;        // per-world-axis effective mass (scalar-per-axis solve)
    private Vector3 _posImpulse;      // split-impulse accumulator (reset each substep; position pass only)

    public void Prepare(RigidBody A, RigidBody B, float h)
    {
        _rA = ImpulseMath.Rotate(A.Orientation, LocalAnchorA);
        _rB = ImpulseMath.Rotate(B.Orientation, LocalAnchorB);
        // Effective mass per world axis d: k = 1/mA + 1/mB + (rA×d)·I⁻¹A(rA×d) + (rB×d)·I⁻¹B(rB×d).
        _mEffDiag = new Vector3(
            EffMassAxis(A, B, new Vector3(1f, 0f, 0f)),
            EffMassAxis(A, B, new Vector3(0f, 1f, 0f)),
            EffMassAxis(A, B, new Vector3(0f, 0f, 1f)));
        _posImpulse = Vector3.Zero;   // reset the split-impulse accumulator; _accImpulse persists (warm-start)
    }

    private float EffMassAxis(RigidBody A, RigidBody B, Vector3 d)
    {
        Vector3 rAxd = Vector3.Cross(_rA, d);
        Vector3 rBxd = Vector3.Cross(_rB, d);
        float k = JointMath.InvMassOf(A) + JointMath.InvMassOf(B)
                + rAxd * JointMath.InvInertiaMul(A, rAxd) + rBxd * JointMath.InvInertiaMul(B, rBxd);
        return k > 0f ? 1f / k : 0f;
    }

    public void WarmStart(RigidBody A, RigidBody B) => ApplyImpulse(A, B, _accImpulse);

    public void SolveVelocity(RigidBody A, RigidBody B)
    {
        // The anchor world points are fixed within a substep (positions only change at integrate), so compute
        // them once; the relative velocity is recomputed per axis (Gauss–Seidel: prior axes' impulses count).
        Vector3 pA = A.Position + _rA, pB = B.Position + _rB;
        SolveVelAxis(A, B, pA, pB, new Vector3(1f, 0f, 0f), _mEffDiag.X);
        SolveVelAxis(A, B, pA, pB, new Vector3(0f, 1f, 0f), _mEffDiag.Y);
        SolveVelAxis(A, B, pA, pB, new Vector3(0f, 0f, 1f), _mEffDiag.Z);
    }

    private void SolveVelAxis(RigidBody A, RigidBody B, Vector3 pA, Vector3 pB, Vector3 d, float mEff)
    {
        float vRel = (B.VelocityAt(pB) - A.VelocityAt(pA)) * d;   // relative anchor velocity along d
        float lambda = -mEff * vRel;                             // BILATERAL: NO clamp
        _accImpulse += d * lambda;
        ApplyImpulse(A, B, d * lambda);
    }

    public void SolvePosition(RigidBody A, RigidBody B, float h)
    {
        // Positional drift C = pB − pA (recomputed from the CURRENT positions; constant across the PosIters loop,
        // like a contact's penetration). Close it with split-impulse pseudo velocities, bias capped to
        // ±MaxBiasSpeed exactly as the contact positional pass does — bilateral, so the bias may be negative.
        Vector3 pA = A.Position + _rA, pB = B.Position + _rB;
        Vector3 c = pB - pA;
        SolvePosAxis(A, B, pA, pB, new Vector3(1f, 0f, 0f), _mEffDiag.X, c, h);
        SolvePosAxis(A, B, pA, pB, new Vector3(0f, 1f, 0f), _mEffDiag.Y, c, h);
        SolvePosAxis(A, B, pA, pB, new Vector3(0f, 0f, 1f), _mEffDiag.Z, c, h);
    }

    private void SolvePosAxis(RigidBody A, RigidBody B, Vector3 pA, Vector3 pB, Vector3 d, float mEff, Vector3 c, float h)
    {
        float bias = Math.Clamp(-(c * d) / h, -ImpulseWorld.MaxBiasSpeed, ImpulseWorld.MaxBiasSpeed);
        float vRelP = (B.PseudoVelocityAt(pB) - A.PseudoVelocityAt(pA)) * d;
        float lambdaP = mEff * (bias - vRelP);
        _posImpulse += d * lambdaP;
        ApplyPseudo(A, B, d * lambdaP);
    }

    // Apply an impulse P at the (coincident) world anchor as a Newton pair: A gets −P, B gets +P.
    private void ApplyImpulse(RigidBody A, RigidBody B, Vector3 p)
    {
        if (JointMath.Movable(A)) { A.LinVel -= p * A.InvMass; A.AngVel -= JointMath.InvInertiaMul(A, Vector3.Cross(_rA, p)); }
        if (JointMath.Movable(B)) { B.LinVel += p * B.InvMass; B.AngVel += JointMath.InvInertiaMul(B, Vector3.Cross(_rB, p)); }
    }

    // Same but writing to the split-impulse PSEUDO velocities (position pass; discarded after integration).
    private void ApplyPseudo(RigidBody A, RigidBody B, Vector3 p)
    {
        if (JointMath.Movable(A)) { A.PseudoLin -= p * A.InvMass; A.PseudoAng -= JointMath.InvInertiaMul(A, Vector3.Cross(_rA, p)); }
        if (JointMath.Movable(B)) { B.PseudoLin += p * B.InvMass; B.PseudoAng += JointMath.InvInertiaMul(B, Vector3.Cross(_rB, p)); }
    }
}

// A ball-socket (point-to-point) joint: keeps A's LocalAnchorA and B's LocalAnchorB coincident, leaving rotation
// free (a ball and socket). A thin wrapper over the shared PointConstraint.
public sealed class BallSocketJoint : Joint
{
    private readonly PointConstraint _point = new();
    public Vector3 LocalAnchorA { get => _point.LocalAnchorA; set => _point.LocalAnchorA = value; }
    public Vector3 LocalAnchorB { get => _point.LocalAnchorB; set => _point.LocalAnchorB = value; }

    public override void Prepare(float h) => _point.Prepare(A, B, h);
    public override void WarmStart() => _point.WarmStart(A, B);
    public override void SolveVelocity() => _point.SolveVelocity(A, B);
    public override void SolvePosition(float h) => _point.SolvePosition(A, B, h);
}

// A HINGE (revolute) joint: the ball-socket point pin PLUS two angular constraints that lock the bodies' relative
// rotation to a single shared axis — the hinged body may rotate ONLY about that axis (a door / trapdoor). The
// point pin removes the 3 translational DOF; the two angular constraints remove the 2 relative-rotation DOF
// PERPENDICULAR to the axis, leaving 1 DOF (free spin about the axis). Those base constraints are BILATERAL
// (unclamped). C1-3a adds two OPTIONAL constraints ON the hinge axis (both default OFF -> a plain hinge steps
// bit-identically to C1-2):
//   • a LIMIT — a UNILATERAL angle stop (clamped like a contact normal: only pushes θ back inside [Lower,Upper]);
//   • a MOTOR — a velocity drive whose accumulated impulse is clamped to ±(MaxMotorTorque·h) (a torque cap).
// Together (motor + limit) they make a servo. All solved in the SAME warm-started PGS loop as contacts.
public sealed class HingeJoint : Joint
{
    private readonly PointConstraint _point = new();
    public Vector3 LocalAnchorA { get => _point.LocalAnchorA; set => _point.LocalAnchorA = value; }
    public Vector3 LocalAnchorB { get => _point.LocalAnchorB; set => _point.LocalAnchorB = value; }
    public Vector3 LocalAxisA;         // hinge axis in A's local frame (unit; coincides with LocalAxisB in world at assembly)
    public Vector3 LocalAxisB;         // hinge axis in B's local frame (unit)

    // ---- angular LIMIT (C1-3a): a unilateral angle stop about the hinge axis, θ relative to the assembly pose ----
    public bool LimitEnabled;
    public float LowerLimit, UpperLimit;   // radians (θ = 0 at assembly); require Lower <= Upper
    // ---- MOTOR (C1-3a): a torque-clamped velocity drive about the hinge axis ----
    public bool MotorEnabled;
    public float MotorTargetSpeed;         // rad/s about the axis (sign = direction)
    public float MaxMotorTorque;           // >= 0 (the torque cap)

    // ---- angular per-substep scratch ----
    private Vector3 _aA, _aB;          // the two hinge axes in world space
    private Vector3 _t1, _t2;          // orthonormal basis ⟂ the hinge axis (the plane rotation is forbidden in)
    private float _invK1, _invK2;      // effective angular masses along t1, t2
    private float _angImp1, _angImp2;  // accumulated axis-lock angular impulses (bilateral, warm-started — persist across substeps)
    private float _angPos1, _angPos2;  // axis-lock angular split-impulse accumulators (reset each substep)

    // ---- hinge-angle / limit / motor scratch (C1-3a) ----
    private bool _refInit;             // reference dirs captured yet? (lazily on the FIRST Prepare -> θ = 0 there)
    private Vector3 _localRefA, _localRefB;   // unit dirs ⟂ the axis in each body's local frame; both = the world t1 at assembly
    private Vector3 _axis;             // the world hinge axis a (= aA) used for θ / motor / limit
    private float _invKAxis;           // effective angular mass ABOUT the hinge axis
    private float _theta;              // current hinge angle about the axis, relative to assembly (θ = 0)
    private float _h;                  // this substep's h (for the motor's torque->impulse clamp in SolveVelocity)
    private int _limitState;           // 0 none / -1 LOWER active / +1 UPPER active (decided in Prepare)
    private float _limitImpulse;       // accumulated limit impulse (unilateral: >=0 lower, <=0 upper), warm-started
    private float _limitPosImpulse;    // accumulated limit split-impulse (position pass; reset each substep)
    private float _motorImpulse;       // accumulated motor impulse (clamped to ±MaxMotorTorque·h), warm-started

    public override void Prepare(float h)
    {
        _point.Prepare(A, B, h);
        _h = h;
        _aA = ImpulseMath.Rotate(A.Orientation, LocalAxisA);
        _aB = ImpulseMath.Rotate(B.Orientation, LocalAxisB);
        // A basis perpendicular to the hinge axis (the two directions rotation is LOCKED in) — reuse contacts'
        // friction-tangent helper so it's deterministic (no frame-to-frame flip for warm-start to catch).
        ImpulseMath.TangentBasis(_aA, out _t1, out _t2);
        _invK1 = InvAngMass(_t1);
        _invK2 = InvAngMass(_t2);
        _angPos1 = 0f; _angPos2 = 0f;   // reset the split-impulse accumulators; _angImp* persist (warm-start)

        // --- C1-3a: hinge angle θ about the axis (0 at assembly) + the axis effective mass ---
        _axis = _aA;
        _invKAxis = InvAngMass(_axis);
        if (!_refInit)
        {
            // capture ⟂-axis reference dirs that BOTH map to the world t1 right now -> θ starts at exactly 0.
            _localRefA = ImpulseMath.RotateInv(A.Orientation, _t1);
            _localRefB = ImpulseMath.RotateInv(B.Orientation, _t1);
            _refInit = true;
        }
        Vector3 dA = ImpulseMath.Rotate(A.Orientation, _localRefA);
        Vector3 dB = ImpulseMath.Rotate(B.Orientation, _localRefB);
        _theta = MathF.Atan2(Vector3.Cross(dA, dB) * _axis, dA * dB);   // signed angle from dA to dB about a

        // --- limit: which bound (if any) is active this substep ---
        int newState = 0;
        if (LimitEnabled)
        {
            if (_theta <= LowerLimit) newState = -1;
            else if (_theta >= UpperLimit) newState = +1;
        }
        if (newState != _limitState) _limitImpulse = 0f;   // (de)activated or flipped side -> drop the stale warm-start
        _limitState = newState;
        _limitPosImpulse = 0f;
        if (!MotorEnabled) _motorImpulse = 0f;             // a disabled motor carries no warm-start impulse
    }

    // Effective angular mass along a world axis p: k = p·I⁻¹A·p + p·I⁻¹B·p (a fixed body contributes 0).
    private float InvAngMass(Vector3 p)
    {
        float k = p * JointMath.InvInertiaMul(A, p) + p * JointMath.InvInertiaMul(B, p);
        return k > 0f ? 1f / k : 0f;
    }

    public override void WarmStart()
    {
        _point.WarmStart(A, B);
        // Re-apply the accumulated angular impulses: the two bilateral axis-lock ones (⟂ the axis), plus (when
        // enabled/active) the motor + limit impulses (ABOUT the axis). Disabled -> exactly the C1-2 expression.
        Vector3 l = _t1 * _angImp1 + _t2 * _angImp2;
        if (MotorEnabled) l += _axis * _motorImpulse;
        if (_limitState != 0) l += _axis * _limitImpulse;
        ApplyAng(l);
    }

    public override void SolveVelocity()
    {
        _point.SolveVelocity(A, B);                  // the point pin first
        SolveAngAxis(_t1, _invK1, ref _angImp1);     // then remove relative angular velocity ⟂ the hinge axis
        SolveAngAxis(_t2, _invK2, ref _angImp2);
        // C1-3a order: MOTOR then LIMIT last, so a limit can arrest the motor (the limit has final say each iter).
        if (MotorEnabled) SolveMotor();
        if (_limitState != 0) SolveLimit();
    }

    // Drive the relative angular velocity along p (⟂ the axis) to zero — bilateral, NO clamp.
    private void SolveAngAxis(Vector3 p, float invK, ref float acc)
    {
        float cdot = (B.AngVel - A.AngVel) * p;
        float lambda = -invK * cdot;
        acc += lambda;
        ApplyAng(p * lambda);
    }

    // MOTOR: drive the relative angular speed about the axis toward MotorTargetSpeed; the accumulated motor
    // impulse is clamped to ±(MaxMotorTorque·h) — a torque cap, so it's a soft (torque-limited) drive.
    private void SolveMotor()
    {
        float rel = (B.AngVel - A.AngVel) * _axis;
        float lambda = _invKAxis * (MotorTargetSpeed - rel);
        float maxImpulse = MaxMotorTorque * _h;
        float old = _motorImpulse;
        _motorImpulse = Math.Clamp(old + lambda, -maxImpulse, maxImpulse);
        ApplyAng(_axis * (_motorImpulse - old));
    }

    // LIMIT (unilateral): once θ is past a bound, arrest further motion INTO the stop only (clamped like a contact
    // normal impulse — never pulls θ the other way). LOWER blocks further decrease (impulse >= 0), UPPER blocks
    // further increase (impulse <= 0).
    private void SolveLimit()
    {
        float rel = (B.AngVel - A.AngVel) * _axis;
        float lambda = _invKAxis * (0f - rel);       // target: bring dθ/dt to 0 at the stop
        float old = _limitImpulse;
        if (_limitState < 0) _limitImpulse = MathF.Max(old + lambda, 0f);   // LOWER: push θ up only
        else                 _limitImpulse = MathF.Min(old + lambda, 0f);   // UPPER: push θ down only
        ApplyAng(_axis * (_limitImpulse - old));
    }

    public override void SolvePosition(float h)
    {
        // Angular drift: the two world hinge axes have separated; err = aA × aB (magnitude ≈ sin of the tilt,
        // direction ⟂ both — so it lies in the t1/t2 plane). Correct it with split-impulse ANGULAR pseudo-velocity
        // along t1, t2; then run the shared point positional pass so the pin gap is corrected too.
        Vector3 err = Vector3.Cross(_aA, _aB);
        SolvePosAngAxis(_t1, _invK1, err, h, ref _angPos1);
        SolvePosAngAxis(_t2, _invK2, err, h, ref _angPos2);
        _point.SolvePosition(A, B, h);
        if (_limitState != 0) SolveLimitPosition(h);   // C1-3a: push θ back to the boundary (split-impulse)
    }

    private void SolvePosAngAxis(Vector3 p, float invK, Vector3 err, float h, ref float acc)
    {
        float bias = Math.Clamp(-(err * p) / h, -ImpulseWorld.MaxBiasSpeed, ImpulseWorld.MaxBiasSpeed);
        float cdot = (B.PseudoAng - A.PseudoAng) * p;
        float lambdaP = invK * (bias - cdot);
        acc += lambdaP;
        ApplyAngPseudo(p * lambdaP);
    }

    // Split-impulse positional push for the LIMIT: mirror the contact penetration bias (Beta·violation/h, capped
    // at MaxBiasSpeed), signed by the bound, with the accumulated pseudo-impulse clamped to the bound's side.
    private void SolveLimitPosition(float h)
    {
        float violation = _limitState < 0 ? LowerLimit - _theta : _theta - UpperLimit;   // > 0 when past the stop
        float mag = Math.Clamp(ImpulseWorld.Beta * MathF.Max(violation, 0f) / h, 0f, ImpulseWorld.MaxBiasSpeed);
        float bias = _limitState < 0 ? mag : -mag;   // LOWER pushes θ up (+), UPPER pushes θ down (−)
        float relP = (B.PseudoAng - A.PseudoAng) * _axis;
        float lambdaP = _invKAxis * (bias - relP);
        float old = _limitPosImpulse;
        if (_limitState < 0) _limitPosImpulse = MathF.Max(old + lambdaP, 0f);
        else                 _limitPosImpulse = MathF.Min(old + lambdaP, 0f);
        ApplyAngPseudo(_axis * (_limitPosImpulse - old));
    }

    // Apply an angular impulse L (about a world axis) as a Newton pair: A gets −L, B gets +L. InvInertiaMul
    // returns 0 for a fixed/sleeping body, so no special-casing is needed.
    private void ApplyAng(Vector3 l)
    {
        A.AngVel -= JointMath.InvInertiaMul(A, l);
        B.AngVel += JointMath.InvInertiaMul(B, l);
    }

    private void ApplyAngPseudo(Vector3 l)
    {
        A.PseudoAng -= JointMath.InvInertiaMul(A, l);
        B.PseudoAng += JointMath.InvInertiaMul(B, l);
    }
}

// A DISTANCE joint: constrains the two anchor points to a fixed rest separation along the line between them.
// Two modes (both BILATERAL — the impulse may push AND pull):
//   • RIGID (default) — a hard rod: a single 1-D velocity constraint holds the anchor separation's rate at 0,
//     plus a split-impulse positional pass that drives the separation to RestLength (mirrors the ball-socket
//     positional pass, but 1-D along the axis toward RestLength instead of an anchor gap toward 0).
//   • SPRING (SpringEnabled && Frequency > 0) — a soft spring-damper toward RestLength, parameterised by
//     Frequency (Hz) + DampingRatio, using the Box2D/Catto SOFT-CONSTRAINT formulation: NO split-impulse pass;
//     the position error is fed back softly through the velocity solve (CFM gamma + a position bias velocity).
// Solved in the SAME warm-started PGS loop as contacts + the other joints. A static/sleeping end contributes 0
// via InvMassOf/InvInertiaMul returning zero — no special-casing.
public sealed class DistanceJoint : Joint
{
    public Vector3 LocalAnchorA;      // anchor in A's local (body) frame
    public Vector3 LocalAnchorB;      // anchor in B's local (body) frame
    public float RestLength;          // the target anchor separation
    public bool SpringEnabled;        // true AND Frequency > 0 -> SOFT spring; otherwise a RIGID rod
    public float Frequency;           // spring frequency (Hz)
    public float DampingRatio;        // spring damping ratio (1 = critical)

    // ---- per-substep scratch ----
    private Vector3 _rA, _rB;         // world lever arms: R·LocalAnchor
    private Vector3 _n;               // constraint axis (unit, A -> B)
    private float _dist;              // current anchor separation
    private float _invMassSum;        // inverse effective mass along n
    private float _acc;               // accumulated scalar impulse along n (warm-started; persists across substeps)
    private bool _spring;             // spring mode active this substep?
    private float _effMass;           // rigid: 1 / invMassSum
    private float _posImpulse;        // rigid: split-impulse accumulator (reset each substep)
    private float _softMass, _gamma, _bias;   // spring: soft-constraint terms

    public override void Prepare(float h)
    {
        _rA = ImpulseMath.Rotate(A.Orientation, LocalAnchorA);
        _rB = ImpulseMath.Rotate(B.Orientation, LocalAnchorB);
        Vector3 pA = A.Position + _rA, pB = B.Position + _rB;
        Vector3 d = pB - pA;
        _dist = d.Length();
        _n = _dist > 1e-9f ? d * (1f / _dist) : new Vector3(1f, 0f, 0f);

        // inverse effective mass along n: 1/mA + 1/mB + (rA×n)·I⁻¹A(rA×n) + (rB×n)·I⁻¹B(rB×n).
        Vector3 rAxn = Vector3.Cross(_rA, _n);
        Vector3 rBxn = Vector3.Cross(_rB, _n);
        _invMassSum = JointMath.InvMassOf(A) + JointMath.InvMassOf(B)
                    + rAxn * JointMath.InvInertiaMul(A, rAxn) + rBxn * JointMath.InvInertiaMul(B, rBxn);

        _spring = SpringEnabled && Frequency > 0f;
        if (_spring)
        {
            // Box2D/Catto soft constraint: with effective mass m = 1/invMassSum, stiffness k = m·ω² and damping
            // c = 2·m·ζ·ω, the CFM gamma = 1/(h(c + h·k)) and the position-bias VELOCITY = C·h·k·gamma (= C·k/(c+hk),
            // Catto's (β/h)·C — units m/s). The velocity solve then feeds C back softly (no split-impulse pass).
            float m = _invMassSum > 0f ? 1f / _invMassSum : 0f;
            float omega = 2f * MathF.PI * Frequency;
            float k = m * omega * omega;
            float c = 2f * m * DampingRatio * omega;
            float denom = h * (c + h * k);
            _gamma = denom > 0f ? 1f / denom : 0f;
            float cErr = _dist - RestLength;
            _bias = cErr * h * k * _gamma;
            _softMass = (_invMassSum + _gamma) > 0f ? 1f / (_invMassSum + _gamma) : 0f;
            // _acc persists (warm-start); the spring has NO positional pass.
        }
        else
        {
            _effMass = _invMassSum > 0f ? 1f / _invMassSum : 0f;
            _posImpulse = 0f;   // reset the split-impulse accumulator; _acc persists (warm-start)
        }
    }

    public override void WarmStart() => Apply(_acc);

    public override void SolveVelocity()
    {
        Vector3 pA = A.Position + _rA, pB = B.Position + _rB;
        float cdot = (B.VelocityAt(pB) - A.VelocityAt(pA)) * _n;   // rate of change of the anchor separation
        float impulse;
        if (_spring)
            impulse = -_softMass * (cdot + _bias + _gamma * _acc);  // soft: CFM feedback + position bias
        else
            impulse = -_effMass * cdot;                             // rigid: hold the length rate at 0 (bilateral)
        _acc += impulse;
        Apply(impulse);
    }

    public override void SolvePosition(float h)
    {
        if (_spring) return;   // spring: position handled softly in the velocity solve (no split-impulse pass)
        // Rigid: drive the separation to RestLength with split-impulse pseudo-velocity (mirror the ball-socket
        // gap pass, 1-D along n). C = dist − RestLength is constant across the PosIters loop (like a contact's pen).
        Vector3 pA = A.Position + _rA, pB = B.Position + _rB;
        float cErr = _dist - RestLength;
        float bias = Math.Clamp(ImpulseWorld.Beta * cErr / h, -ImpulseWorld.MaxBiasSpeed, ImpulseWorld.MaxBiasSpeed);
        float pseudoCdot = (B.PseudoVelocityAt(pB) - A.PseudoVelocityAt(pA)) * _n;
        float lambdaP = -_effMass * (pseudoCdot + bias);
        _posImpulse += lambdaP;
        ApplyPseudo(lambdaP);
    }

    // Apply a scalar impulse P along the axis n at the anchors (Newton pair, exactly like the ball-socket pin):
    // A gets −P·n, B gets +P·n. InvMassOf/InvInertiaMul return 0 for a fixed/sleeping body, so no special-casing.
    private void Apply(float p)
    {
        Vector3 impulse = _n * p;
        A.LinVel -= impulse * JointMath.InvMassOf(A);
        A.AngVel -= JointMath.InvInertiaMul(A, Vector3.Cross(_rA, impulse));
        B.LinVel += impulse * JointMath.InvMassOf(B);
        B.AngVel += JointMath.InvInertiaMul(B, Vector3.Cross(_rB, impulse));
    }

    // Same but writing to the split-impulse PSEUDO velocities (rigid position pass; discarded after integration).
    private void ApplyPseudo(float p)
    {
        Vector3 impulse = _n * p;
        A.PseudoLin -= impulse * JointMath.InvMassOf(A);
        A.PseudoAng -= JointMath.InvInertiaMul(A, Vector3.Cross(_rA, impulse));
        B.PseudoLin += impulse * JointMath.InvMassOf(B);
        B.PseudoAng += JointMath.InvInertiaMul(B, Vector3.Cross(_rB, impulse));
    }
}
