using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RigidbodySetVelocity : MonoBehaviour
{
    // Start is called before the first frame update
   public float forcemult= 200;
   private Rigidbody rb;

   private void awake()
    {
        rb= GetComponent<Rigidbody>();
        rb.velocity= transform.forward*Time.deltaTime*forcemult;
    }

    // Update is called once per frame
    private void Update()
    {
        
    }
}
