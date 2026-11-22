from pathlib import Path
from xml.sax.saxutils import escape
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT
from reportlab.lib import colors
from reportlab.lib.units import inch

root = Path(__file__).parent
lines = (root / 'plan.md').read_text(encoding='utf-8').splitlines()
doc = Document()
sec = doc.sections[0]
sec.page_width, sec.page_height = Inches(8.5), Inches(11)
sec.top_margin, sec.bottom_margin = Inches(.78), Inches(.75)
sec.left_margin = sec.right_margin = Inches(.85)
doc.styles['Normal'].font.name = 'Calibri'
doc.styles['Normal'].font.size = Pt(10.5)
doc.styles['Normal'].paragraph_format.space_after = Pt(5)
doc.styles['Heading 1'].font.color.rgb = RGBColor.from_string('17365D')
styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name='TTitle', parent=styles['Title'], fontSize=19, leading=23, textColor=colors.black, spaceAfter=10, alignment=TA_LEFT))
styles.add(ParagraphStyle(name='THead', parent=styles['Heading2'], fontSize=13, leading=16, textColor=colors.HexColor('#17365D'), spaceBefore=10, spaceAfter=5))
styles.add(ParagraphStyle(name='TBody', parent=styles['BodyText'], fontSize=10, leading=13, spaceAfter=5))
styles.add(ParagraphStyle(name='TBullet', parent=styles['TBody'], leftIndent=14, firstLineIndent=-9))
story=[]
for line in lines:
    if not line.strip(): continue
    if line.startswith('# '):
        value=line[2:]; doc.add_heading(value, 0); story.append(Paragraph(escape(value), styles['TTitle']))
    elif line.startswith('## '):
        value=line[3:]; doc.add_heading(value, 1); story.append(Paragraph(escape(value), styles['THead']))
    else:
        bullet=line.startswith('- '); value=line[2:] if bullet else line
        value=value.replace('**','')
        p=doc.add_paragraph(value, style='List Bullet' if bullet else None); p.paragraph_format.keep_together=True
        story.append(Paragraph(('&#8226;  ' if bullet else '')+escape(value), styles['TBullet'] if bullet else styles['TBody']))
foot=sec.footer.paragraphs[0]; foot.text='Tether Bridge completion plan'; foot.runs[0].font.size=Pt(8)
doc.save(root/'TetherBridge_PdaNet_Completion_Plan.docx')
def footer(canvas,document):
    canvas.setFont('Helvetica',8); canvas.setFillColor(colors.grey)
    canvas.drawString(.85*inch,.48*inch,'Tether Bridge completion plan'); canvas.drawRightString(7.65*inch,.48*inch,str(document.page))
SimpleDocTemplate(str(root/'TetherBridge_PdaNet_Completion_Plan.pdf'),pagesize=(612,792),leftMargin=.85*inch,rightMargin=.85*inch,topMargin=.75*inch,bottomMargin=.72*inch,title='Tether Bridge PdaNet Completion Plan').build(story,onFirstPage=footer,onLaterPages=footer)
print('Created DOCX and PDF')
