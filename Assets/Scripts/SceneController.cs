using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;
using Oculus.Interaction;
using Unity.VisualScripting;
using Oculus.Interaction.Surfaces;
using UnityEngine.Animations;

class StreamMessage {
    public Dictionary<string, List<float>> updateData;
    public float time;
}

public class SceneController : MonoBehaviour
{   
    private float lastSimulationTimeStamp = 0.0f;
    public Dictionary<string, Transform> _objectsTrans;

    public void StartUpdate(Dictionary<string, Transform> objectsTrans) {
        _objectsTrans = objectsTrans;
    }

    public void listener(string message) {

        if (string.Compare(message, "END") == 0) {
            lastSimulationTimeStamp = 0.0f;
            return;
        }

        StreamMessage streamMsg = JsonConvert.DeserializeObject<StreamMessage>(message);

        if (streamMsg.time < lastSimulationTimeStamp) return;
        lastSimulationTimeStamp = streamMsg.time;
        foreach (var (name, value) in streamMsg.updateData) {
            _objectsTrans[name].position = transform.TransformPoint(new Vector3(value[0], value[1], value[2]));
            _objectsTrans[name].rotation = transform.rotation * new Quaternion(value[3], value[4], value[5], value[6]);
        }
    }

}
