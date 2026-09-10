from pathlib import Path
import subprocess,sys,os,json
root=Path('/home/ec2-user/ug-fluidio-wt')
os.chdir(root)
status=root/'.verify/fluid_third_render_status.json'
steps=[('angles',[sys.executable,'tools/fluid_contact_sheet.py'],{})]
steps += [('icons',[sys.executable,'tools/fluid_item_icons.py','--devices','9111,9112,9113,9114,9116,9117,9121'],{})]
for i in [9111,9112,9113,9114,9116,9117,9121]:
 steps.append((f'lod1-{i}',[sys.executable,'tools/shot.py','fluidlod','-o',str(root/f'notes/fluid_angles/lod1/{i}.png')],{'DEVICE':str(i)}))
steps += [('pump-suction',[sys.executable,'tools/shot.py','fluiddevice','-o',str(root/'notes/fluid_angles/9114_suction.png')],{'DEVICE':'9114','ANGLE':'270','ELEV':'22'}),('catcher-drain',[sys.executable,'tools/shot.py','fluiddevice','-o',str(root/'notes/fluid_angles/9111_drain.png')],{'DEVICE':'9111','ANGLE':'35','ELEV':'55'})]
completed=[]
try:
 for name,cmd,env in steps:
  status.write_text(json.dumps({'running':name,'completed':completed})+'\n')
  print('BEGIN',name,flush=True)
  subprocess.run(cmd,env=dict(os.environ,**env),check=True)
  completed.append(name)
 status.write_text(json.dumps({'done':True,'completed':completed})+'\n')
except Exception as e:
 status.write_text(json.dumps({'error':str(e),'completed':completed})+'\n')
 raise
