using UnityEngine;

public class CountDebuger
{
    float waittime;
    float passedtime;
    public CountDebuger(float time)
    {
        waittime = time;
    }
    public void ShowMessage(object message,float time)
    {
        passedtime += time;
        if (passedtime > waittime)
        {
            passedtime = 0f;
            Debug.Log(message);
        }
    }
}
