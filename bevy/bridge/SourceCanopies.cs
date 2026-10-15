namespace UnturnedBevy;

public sealed partial class SourceCanopies
{
    readonly Dictionary<int,int> _indices=new();
    public void Set(int index,float x,float y,float z,float horizontalScale,float verticalScale)
    {
        if(!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(z)||!float.IsFinite(horizontalScale)||!float.IsFinite(verticalScale)
            ||horizontalScale<0||verticalScale<0)throw new ArgumentException("Invalid original tree canopy transform");
        var c=new Canopy{X=x,Z=z,R=CanopyRadius*horizontalScale,YLo=y+CanopyLowY*verticalScale,YHi=y+CanopyHighY*verticalScale};
        if(_indices.TryGetValue(index,out int slot))_canopies[slot]=c;else{_indices[index]=_canopies.Count;_canopies.Add(c);}
    }
    public void Clear(){_canopies.Clear();_indices.Clear();}
    public void Translate(float x,float z,float y=0)
    {for(int i=0;i<_canopies.Count;i++){var c=_canopies[i];c.X+=x;c.Z+=z;c.YLo+=y;c.YHi+=y;_canopies[i]=c;}}
}
