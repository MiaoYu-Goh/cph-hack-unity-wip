using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class NativeArrayError : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
    
    void foo(NativeArray<int> a, ref int i)
    {
        ref int e = ref a[0];
        a.Dispose();
        i = e; // can this trigger an analyzer error? "NativeArray reference used after Dispose"
    }
}

