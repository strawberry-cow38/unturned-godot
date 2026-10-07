using Godot;
using System;

namespace UnturnedGodot
{
    public partial class Vehicle
    {
        // Offline showcase only. Matching alternate assets are installed temporarily by the study
        // runner; no new SpecNames entry, item id, networking registration or normal-build override.
        public static Vehicle BuildSedanMk2LongNoseStudy(int variant=5)
        {
            var s=SedanMk2Spec;
            s.BoxSize += new Vector3(0,0,.36f);
            s.BoxCenter += new Vector3(0,0,-.18f);
            s.Wheels=((float,float,float,bool)[])s.Wheels.Clone();
            for(int i=0;i<2;i++)
            {
                var w=s.Wheels[i];s.Wheels[i]=(w.Item1,w.Item2,w.Item3-.36f,w.Item4);
            }
            var defs=new AuthoredPanelDef[s.AuthoredPanels.Length];
            for(int i=0;i<defs.Length;i++)
            {
                var d=s.AuthoredPanels[i];var pivot=d.Pivot;
                if(d.PanelIndex<2)pivot.Z=-1.41722f;
                if(d.PanelIndex==4)pivot.Z-=.36f;
                defs[i]=new AuthoredPanelDef(d.MeshPath,d.PanelIndex,d.SeatIndex,pivot,d.Axis,d.Degrees,d.GlassLabel);
            }
            s.AuthoredPanels=defs;
            s.SpotPos=Array.ConvertAll(s.SpotPos,p=>p+new Vector3(0,0,-.36f));
            s.OmniPos+=new Vector3(0,0,-.36f);
            // Same display/spec key preserves original tuned seat-body offsets. This constructor
            // is called only by --sedan-mk2-showcase with UG_MK2_LONG_NOSE_STUDY=1, offline.
            return Build(s,variant,"sedan_mk2");
        }
    }
}
