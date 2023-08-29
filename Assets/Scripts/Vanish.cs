using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Vanish : MonoBehaviour
{
    public GameObject p3;

    void OnCollisionEnter(Collision col)
    {
        if (col.gameObject.name == "Ball")
        {
            Debug.Log("Collision detected");
            p3.GetComponent<Renderer>().enabled = false;
        }
    }
}
