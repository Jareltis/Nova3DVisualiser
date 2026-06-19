namespace Nova3DVisualiser;

public struct FacingInfo
{
    public readonly int Vertex1;
    public readonly int Vertex2;
    public readonly int Vertex3;
    public readonly int Normal1;
    public readonly int Normal2;
    public readonly int Normal3;

    // Flat: one shared normal for all three vertices (manually-built primitives: cube/plane).
    public FacingInfo(int[] vertexIndex, int normalIndex)
    {
        Vertex1 = vertexIndex[0];
        Vertex2 = vertexIndex[1];
        Vertex3 = vertexIndex[2];
        Normal1 = Normal2 = Normal3 = normalIndex;
    }

    // Smooth: a separate normal per vertex (OBJ loader).
    public FacingInfo(int[] vertexIndex, int[] normalIndex)
    {
        Vertex1 = vertexIndex[0];
        Vertex2 = vertexIndex[1];
        Vertex3 = vertexIndex[2];
        Normal1 = normalIndex[0];
        Normal2 = normalIndex[1];
        Normal3 = normalIndex[2];
    }
}
