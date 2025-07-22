using System.Collections;
using NaughtyAttributes;
using UnityEngine;
class PerfectLOD : MonoBehaviour
{
    [SerializeField, BoxGroup("Settings")] float DistanceOffAll;
    [ReadOnly, SerializeField, BoxGroup("Settings")] float cameraDistance;
    [SerializeField, BoxGroup("Objects"), ShowAssetPreview] GameObject[] GameObject;
    [SerializeField, BoxGroup("Objects")] float DistanceToObjects;

    [SerializeField, BoxGroup("Objects"), ShowAssetPreview] GameObject[] GameObjectLOD1;
    [Space]
    [SerializeField, BoxGroup("Objects")] GameObject[] SpecificGameObject;
    [SerializeField, BoxGroup("Objects")] float DistanceToSpecificObjects;
    [SerializeField, BoxGroup("Objects")] Light[] lights;
    [SerializeField, BoxGroup("Objects")] float DistanceToLight;
    [Space]
    [SerializeField, BoxGroup("Objects")] ParticleSystem[] particleSystems;
    [SerializeField, BoxGroup("Objects")] float DistanceToParticle;
    [Space]
    [SerializeField, BoxGroup("Objects")] AudioSource[] AudioSource;
    [SerializeField, BoxGroup("Objects")] float DistanceToAudio;
    [ReadOnly, SerializeField] Camera Camera;
#if UNITY_EDITOR
    private void OnValidate() {
        if(!Camera)
            Camera = Camera.main;
    }
#endif
    private void Start() {
        StartCoroutine(UpdateDistace());
    }

    IEnumerator UpdateDistace()
    {
        if(!Camera) yield return null;
        cameraDistance = Vector3.Distance(Camera.gameObject.transform.position, transform.position);
        for (int i = 0; i < GameObject.Length; i++)
        {
            if(cameraDistance >= DistanceToObjects){
                if(GameObject[i].activeInHierarchy)
                    GameObject[i].SetActive(false);                 // GAME OBJECTS
            }
                
            else {
                if(!GameObject[i].activeInHierarchy)
                    GameObject[i].SetActive(true);
            }
        }
        if(GameObjectLOD1.Length >= 0){
            for (int i = 0; i < GameObjectLOD1.Length; i++)
            {
                if(cameraDistance >= DistanceToObjects){
                    if(!GameObjectLOD1[i].activeInHierarchy)
                        GameObjectLOD1[i].SetActive(true);              // GAME OBJECTS LOD 1
                }
                    
                else {
                    if(GameObjectLOD1[i].activeInHierarchy)
                        GameObjectLOD1[i].SetActive(false);
                }
            }            
        }
        for (int i = 0; i < SpecificGameObject.Length; i++)
        {
            if(cameraDistance >= DistanceToSpecificObjects){
                if(SpecificGameObject[i].activeInHierarchy)
                    SpecificGameObject[i].SetActive(false);              // SPECIFIC OBJECT
            }
                
            else {
                if(!SpecificGameObject[i].activeInHierarchy)
                    SpecificGameObject[i].SetActive(true);
            }
        }
        for (int i = 0; i < lights.Length; i++)
        {
            if(cameraDistance >= DistanceToLight){
                if(lights[i].enabled)
                    lights[i].enabled = false;                      // LIGHT
            }
                                
            else {
                if(!lights[i].enabled)
                    lights[i].enabled = true;
            }
        }
        for (int i = 0; i < particleSystems.Length; i++)
        {
            if(cameraDistance >= DistanceToParticle){
                if(particleSystems[i].isPlaying)
                    particleSystems[i].Stop();                      // PARTICLE
            }
                
            else {
                if(!particleSystems[i].isPlaying)
                    particleSystems[i].Play();
            }
        }
        for (int i = 0; i < AudioSource.Length; i++)
        {
            if(cameraDistance >= DistanceToAudio){
                if(AudioSource[i].isPlaying) 
                    AudioSource[i].Stop();                          // AUDIO
            }
            else {
                if(!AudioSource[i].isPlaying) 
                    AudioSource[i].Play();
            }
        }
        yield return new WaitForSeconds(1);
        StartCoroutine(UpdateDistace());
    }
}
