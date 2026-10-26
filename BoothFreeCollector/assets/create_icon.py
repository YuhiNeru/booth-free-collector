"""Generate the original download icon. Requires Pillow only to rebuild assets."""
from pathlib import Path
from PIL import Image,ImageDraw
root=Path(__file__).resolve().parent
svg='''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256"><rect x="8" y="8" width="240" height="240" rx="56" fill="#15202b"/><path d="M128 52v100m-40-38 40 40 40-40" fill="none" stroke="#f5f8fa" stroke-width="20" stroke-linecap="round" stroke-linejoin="round"/><path d="M64 176v24h128v-24" fill="none" stroke="#1da1f2" stroke-width="16" stroke-linecap="round" stroke-linejoin="round"/></svg>'''
(root/'app.svg').write_text(svg,encoding='utf-8')
scale=4
im=Image.new('RGBA',(256*scale,256*scale))
d=ImageDraw.Draw(im)
def box(a):return tuple(int(x*scale) for x in a)
d.rounded_rectangle(box((8,8,248,248)),radius=56*scale,fill='#15202b')
def stroke(points,color,width):
 pts=[(x*scale,y*scale) for x,y in points];d.line(pts,fill=color,width=width*scale,joint='curve')
 for x,y in points:d.ellipse(box((x-width/2,y-width/2,x+width/2,y+width/2)),fill=color)
stroke([(128,52),(128,152)],'#f5f8fa',20)
stroke([(88,114),(128,154),(168,114)],'#f5f8fa',20)
stroke([(64,176),(64,200),(192,200),(192,176)],'#1da1f2',16)
im=im.resize((256,256),Image.Resampling.LANCZOS)
im.save(root/'app.png')
im.save(root/'app.ico',sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
print('Created SVG, PNG and multi-resolution Windows icon')
