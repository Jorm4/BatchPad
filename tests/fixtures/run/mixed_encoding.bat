@echo off
rem Saved in code page 850: cmd echoes OEM bytes, Python prints UTF-8.
echo OEM „Ž”
py -3 -c "print('UTF-8 \u00e4\u20ac')"
