namespace MabiAgent.Desktop.Editors;

public readonly record struct SelectionBounds(int Left,int Top,int Right,int Bottom)
{
    public int Width=>Right-Left;
    public int Height=>Bottom-Top;
}

/// <summary>Physical client pixels are the source of truth; DPI never multiplies an already physical point.</summary>
public static class EditorSelectionGeometry
{
    public static (int X,int Y) ClientPoint(int screenX,int screenY,int originX,int originY,int width,int height)
    {
        if(width<=0||height<=0)throw new ArgumentException("客户区尺寸无效");
        return(Math.Clamp(screenX-originX,0,width-1),Math.Clamp(screenY-originY,0,height-1));
    }
    public static SelectionBounds FromDrag(int startX,int startY,int endX,int endY,int width,int height,bool circle=false)
    {
        if(width<=0||height<=0)throw new ArgumentException("客户区尺寸无效");
        startX=Math.Clamp(startX,0,width-1);startY=Math.Clamp(startY,0,height-1);endX=Math.Clamp(endX,0,width-1);endY=Math.Clamp(endY,0,height-1);
        if(circle){var side=Math.Min(Math.Abs(endX-startX),Math.Abs(endY-startY));endX=startX+Math.Sign(endX-startX)*side;endY=startY+Math.Sign(endY-startY)*side;}
        return new(Math.Min(startX,endX),Math.Min(startY,endY),Math.Max(startX,endX)+1,Math.Max(startY,endY)+1);
    }
    public static SelectionBounds ToReference(SelectionBounds native,int actualWidth,int actualHeight,int referenceWidth,int referenceHeight)
    {
        if(actualWidth<=0||actualHeight<=0||referenceWidth<=0||referenceHeight<=0)throw new ArgumentException("客户区尺寸无效");
        if(native.Width<=0||native.Height<=0||native.Left<0||native.Top<0||native.Right>actualWidth||native.Bottom>actualHeight)throw new ArgumentException("选区越出客户区");
        int l=Math.Clamp((int)Math.Floor(native.Left*(double)referenceWidth/actualWidth),0,referenceWidth-1),t=Math.Clamp((int)Math.Floor(native.Top*(double)referenceHeight/actualHeight),0,referenceHeight-1);
        int r=Math.Clamp((int)Math.Ceiling(native.Right*(double)referenceWidth/actualWidth),l+1,referenceWidth),b=Math.Clamp((int)Math.Ceiling(native.Bottom*(double)referenceHeight/actualHeight),t+1,referenceHeight);
        return new(l,t,r,b);
    }
}
