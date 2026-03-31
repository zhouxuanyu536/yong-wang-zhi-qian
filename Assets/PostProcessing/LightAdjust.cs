using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
[ExecuteInEditMode]
public class LightAdjust : MonoBehaviour
{
    private Volume volume;
    [SerializeField, Range(0, 1)] private float setBright;
    // Start is called before the first frame update
    void Start()
    {
        volume = GetComponent<Volume>();
    }
    
    // Update is called once per frame
    void Update()
    {
        if (volume.profile.TryGet(out LiftGammaGain liftGammaGain))
        {
            liftGammaGain.gain.value = new Vector4(1, 1, 1, GlobalSettings.bright * 0.5f - 0.5f);
        }
    }
}
