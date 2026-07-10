using System;
using Nova3DVisualiser;
using SampleGame.Worlds;

namespace SampleGame.Physics;

// C1-4a: translate a serialized JointConfig into a live solver Joint. PURE — no scene/world access; the caller
// supplies a `resolveBody` that maps an object id to its RigidBody (id == -1 -> a fixed WORLD-anchor body). Kept
// pure so it's unit-testable and reusable by the physics bridge.
public static class JointBuilder
{
    // Build the Joint for `cfg`, resolving both endpoints via `resolveBody`. Returns null (caller skips + logs)
    // if either body doesn't resolve or the Kind is unknown.
    //
    // NOTE on the WORLD anchor (BodyId -1): `resolveBody(-1)` is expected to return a static body AT THE ORIGIN
    // (identity). This translator always copies AnchorA/AnchorB into the joint's LocalAnchorA/B, so for a -1
    // endpoint the anchor is `origin + AnchorA` = AnchorA — i.e. AnchorA is the WORLD position (matching the
    // JointConfig doc). For a real body (id >= 0) it's the body's LOCAL-frame anchor, as usual.
    public static Joint? BuildJoint(JointConfig cfg, Func<int, RigidBody?> resolveBody)
    {
        RigidBody? a = resolveBody(cfg.BodyA);
        RigidBody? b = resolveBody(cfg.BodyB);
        if (a == null || b == null) return null;

        Vector3 anchorA = ToVec(cfg.AnchorA);
        Vector3 anchorB = ToVec(cfg.AnchorB);

        switch (cfg.Kind?.ToLowerInvariant())
        {
            case "ballsocket":
                return new BallSocketJoint { A = a, B = b, LocalAnchorA = anchorA, LocalAnchorB = anchorB };

            case "hinge":
            {
                Vector3 axis = ToVec(cfg.Axis);
                return new HingeJoint
                {
                    A = a, B = b, LocalAnchorA = anchorA, LocalAnchorB = anchorB,
                    LocalAxisA = axis, LocalAxisB = axis,
                    LimitEnabled = cfg.LimitEnabled, LowerLimit = cfg.LowerLimit, UpperLimit = cfg.UpperLimit,
                    MotorEnabled = cfg.MotorEnabled, MotorTargetSpeed = cfg.MotorTargetSpeed, MaxMotorTorque = cfg.MaxMotorTorque,
                };
            }

            case "distance":
                return new DistanceJoint
                {
                    A = a, B = b, LocalAnchorA = anchorA, LocalAnchorB = anchorB,
                    RestLength = cfg.RestLength,
                    SpringEnabled = cfg.SpringEnabled, Frequency = cfg.Frequency, DampingRatio = cfg.DampingRatio,
                };

            default:
                return null;   // unknown Kind
        }
    }

    private static Vector3 ToVec(Vec3Config v) => new Vector3(v.X, v.Y, v.Z);
}
