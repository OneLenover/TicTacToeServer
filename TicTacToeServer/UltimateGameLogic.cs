namespace TicTacToeServer.Logic
{
    public class UltimateGameLogic
    {
        public char[] Cells { get; set; } = Enumerable.Repeat('.', 81).ToArray();
        public char[] SmallWinners { get; set; } = Enumerable.Repeat('.', 9).ToArray();
        public int ActiveBoardX { get; set; } = -1;
        public int ActiveBoardY { get; set; } = -1;
        public string PlayerX { get; set; } = "";
        public string PlayerO { get; set; } = ""; 
        public bool IsXTurn { get; set; } = true;
        public string Status { get; set; } = "Playing";

        public bool ValidMove(int bX, int bY, int cX, int cY, string pId)
        {
            if (Status != "Playing") return false;
            
            string expectedId = IsXTurn ? PlayerX : PlayerO;
            if (pId != expectedId) return false;

            if (ActiveBoardX != -1 && (bX != ActiveBoardX || bY != ActiveBoardY)) return false;
            if (SmallWinners[bY * 3 + bX] != '.') return false;
            if (Cells[(bY * 3 + cY) * 9 + (bX * 3 + cX)] != '.') return false;
            return true;
        }

        public void MakeMove(int bX, int bY, int cX, int cY)
        {
            char symbol = IsXTurn ? 'X' : 'O';
            Cells[(bY * 3 + cY) * 9 + (bX * 3 + cX)] = symbol;

            if (CheckWin(GetSmallBoard(bX, bY))) 
                SmallWinners[bY * 3 + bX] = symbol;

            if (SmallWinners[cY * 3 + cX] == '.' && GetSmallBoard(cX, cY).Contains('.'))
            {
                ActiveBoardX = cX; ActiveBoardY = cY;
            }
            else
            {
                ActiveBoardX = -1; ActiveBoardY = -1;
            }

            if (CheckWin(SmallWinners)) 
                Status = symbol == 'X' ? "X_Won" : "O_Won";
            else if (!Cells.Contains('.')) 
                Status = "Draw";

            IsXTurn = !IsXTurn;
        }

        private char[] GetSmallBoard(int bX, int bY)
        {
            char[] b = new char[9];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    b[y * 3 + x] = Cells[(bY * 3 + y) * 9 + (bX * 3 + x)];
            return b;
        }

        private bool CheckWin(char[] b)
        {
            int[][] w = { new[] {0,1,2}, new[] {3,4,5}, new[] {6,7,8}, new[] {0,3,6}, new[] {1,4,7}, new[] {2,5,8}, new[] {0,4,8}, new[] {2,4,6} };
            return w.Any(p => b[p[0]] != '.' && b[p[0]] == b[p[1]] && b[p[0]] == b[p[2]]);
        }
    }
}