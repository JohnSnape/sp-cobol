000010*>-----------------------------------------------------------------
000020*> Your first COBOL screen program.
000030*>
000040*> Press F5 to run it.  The project builds two programs from this
000050*> one file (ScreenMode = Both in the project file):
000060*>     CobolApp1.exe      a DOS-style text screen
000070*>     CobolApp1-Win.exe  the same screen in a window
000080*> Pick which one F5 starts in the toolbar's run-profile list.
000090*>
000100*> Layout reminder (fixed format): columns 1-6 are the sequence
000110*> number (000010, 000020, ... by tens, so new lines fit between),
000120*> column 7 is '*' for a comment, code goes in columns 8-72.
000130*>-----------------------------------------------------------------
000140 IDENTIFICATION DIVISION.
000150 PROGRAM-ID. WELCOME.
000160
000170 ENVIRONMENT DIVISION.
000180 CONFIGURATION SECTION.
000190 SPECIAL-NAMES.
000200     CRT STATUS IS WS-KEY.
000210
000220 DATA DIVISION.
000230 WORKING-STORAGE SECTION.
000240 01  WS-KEY              PIC 9(4).
000250 01  WS-NAME             PIC X(20).
000260 01  WS-HOURS            PIC 9(3)V9    VALUE 40.
000270 01  WS-RATE             PIC 9(3)V99   VALUE 15.00.
000280 01  WS-PAY              PIC 9(5)V99.
000290 01  WS-PAY-SHOWN        PIC $$$,$$9.99.
000300
000310 SCREEN SECTION.
000320 01  PAY-SCREEN BLANK SCREEN
000330         FOREGROUND-COLOR 7 BACKGROUND-COLOR 1.
000340     05  LINE 2  COL 25 VALUE "WEEKLY PAY CALCULATOR" HIGHLIGHT.
000350     05  LINE 5  COL 10 VALUE "Your name:".
000360     05  LINE 5  COL 25 PIC X(20) USING WS-NAME REQUIRED.
000370     05  LINE 7  COL 10 VALUE "Hours worked:".
000380     05  LINE 7  COL 25 PIC ZZ9.9 USING WS-HOURS.
000390     05  LINE 9  COL 10 VALUE "Hourly rate:".
000400     05  LINE 9  COL 25 PIC ZZ9.99 USING WS-RATE.
000410     05  LINE 12 COL 10 VALUE "Weekly pay:".
000420     05  LINE 12 COL 25 PIC X(11) FROM WS-PAY-SHOWN HIGHLIGHT.
000430     05  LINE 22 COL 10
000440         VALUE "Enter = calculate     Esc = quit".
000450
000460 PROCEDURE DIVISION.
000470 MAIN-PARAGRAPH.
000480     PERFORM UNTIL WS-KEY = 2005
000490         DISPLAY PAY-SCREEN
000500         ACCEPT PAY-SCREEN
000510         IF WS-KEY = 0
000520             PERFORM CALCULATE-PAY
000530         END-IF
000540     END-PERFORM
000550     GOBACK.
000560
000570 CALCULATE-PAY.
000580     IF WS-HOURS > 40
000590         COMPUTE WS-PAY ROUNDED = WS-RATE * 40
000600             + WS-RATE * 1.5 * (WS-HOURS - 40)
000610     ELSE
000620         COMPUTE WS-PAY ROUNDED = WS-RATE * WS-HOURS
000630     END-IF
000640     MOVE WS-PAY TO WS-PAY-SHOWN.
