// Copyright (c) 2026 Pokahi. Licensed under the MIT License.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace Pokahi.MmdCameraTools
{
public static class CameraAdjustment
{
 static bool Finite(float v){return !float.IsNaN(v)&&!float.IsInfinity(v);}
 public static void Adjust(Transform pivot,Transform camera,float scale,float height,float distance,float yaw,float horizontal=0)
 {
  var turn=Quaternion.Euler(0,yaw,0);
  pivot.localPosition=turn*(pivot.localPosition*scale)+Vector3.up*height;
  pivot.localRotation=turn*pivot.localRotation;
  camera.localPosition*=scale*distance;
  // Translate along the final camera right axis without changing its rotation.
  pivot.localPosition+=(pivot.localRotation*camera.localRotation*Vector3.right)*horizontal;
 }
 public static void Validate(AnimationClip clip)
 {
  if(clip==null)throw new Exception("カメラのAnimationClipを指定してください。");
  var bindings=AnimationUtility.GetCurveBindings(clip);
  if(AnimationUtility.GetObjectReferenceCurveBindings(clip).Length!=0)throw new Exception("オブジェクト参照カーブは対応していません。");
  if(bindings.Length==0||bindings.Any(b=>!((b.type==typeof(Transform)&&(b.path=="Pivot"||b.path=="Pivot/Camera")&&(b.propertyName.StartsWith("m_LocalPosition.")||b.propertyName.StartsWith("m_LocalRotation.")||b.propertyName.StartsWith("localEulerAngles")))||(b.type==typeof(Camera)&&b.path=="Pivot/Camera"&&b.propertyName=="field of view"))))
   throw new Exception("対応する Pivot / Camera 構造のカメラを指定してください（大文字・小文字を区別）。");
 }
 public static AnimationClip Create(AnimationClip source,float scale,float height,float distance,float yaw,float horizontal=0)
 {
  Validate(source);
  if(!Finite(scale)||!Finite(height)||!Finite(distance)||!Finite(yaw)||!Finite(horizontal)||scale<=0||distance<=0)throw new Exception("数値は有限値、倍率は0より大きい値を指定してください。");
  var result=Object.Instantiate(source);result.name=source.name+"_Adjusted";
  // Sample adjusted transforms at the source frame rate (up to 120 fps).
  // Field-of-view and child rotation curves retain their original keys.
  var go=new GameObject("Temporary camera conversion"){hideFlags=HideFlags.HideAndDontSave};
  var pivot=new GameObject("Pivot").transform;pivot.SetParent(go.transform,false);
  var child=new GameObject("Camera").transform;child.SetParent(pivot,false);child.localRotation=Quaternion.Euler(0,180,0);child.gameObject.AddComponent<Camera>().enabled=false;
  try{
   float sampleRate=Finite(source.frameRate)&&source.frameRate>0?Mathf.Clamp(source.frameRate,1,120):30;
   int count=Mathf.CeilToInt(source.length*sampleRate)+1;
   if(count>200000)throw new Exception("カメラが長すぎます。クリップを分割してください。");
   var keys=new Keyframe[10][];for(int i=0;i<keys.Length;i++)keys[i]=new Keyframe[count];
   float fps=sampleRate;
   for(int n=0;n<count;n++){
    float t=Mathf.Min(n/fps,source.length);pivot.localPosition=Vector3.zero;pivot.localRotation=Quaternion.identity;child.localPosition=Vector3.zero;child.localRotation=Quaternion.Euler(0,180,0);
    source.SampleAnimation(go,t);Adjust(pivot,child,scale,height,distance,yaw,horizontal);
    for(int j=0;j<3;j++){keys[j][n]=new Keyframe(t,pivot.localPosition[j]);keys[7+j][n]=new Keyframe(t,child.localPosition[j]);}
    for(int j=0;j<4;j++)keys[3+j][n]=new Keyframe(t,pivot.localRotation[j]);
   }
   // Remove Euler curves before writing a quaternion rotation representation.
   foreach(var b in AnimationUtility.GetCurveBindings(result).Where(b=>b.path=="Pivot"&&b.type==typeof(Transform)&&(b.propertyName.Contains("Euler")||b.propertyName.StartsWith("m_LocalRotation."))))AnimationUtility.SetEditorCurve(result,b,null);
   string[] props={"m_LocalPosition.x","m_LocalPosition.y","m_LocalPosition.z","m_LocalRotation.x","m_LocalRotation.y","m_LocalRotation.z","m_LocalRotation.w","m_LocalPosition.x","m_LocalPosition.y","m_LocalPosition.z"};
   var curves=new AnimationCurve[10];
   for(int i=0;i<10;i++){
    curves[i]=new AnimationCurve(keys[i]);
    for(int k=0;k<curves[i].length;k++){AnimationUtility.SetKeyLeftTangentMode(curves[i],k,AnimationUtility.TangentMode.Linear);AnimationUtility.SetKeyRightTangentMode(curves[i],k,AnimationUtility.TangentMode.Linear);}
    AnimationUtility.SetEditorCurve(result,EditorCurveBinding.FloatCurve(i<7?"Pivot":"Pivot/Camera",typeof(Transform),props[i]),curves[i]);
   }
   result.EnsureQuaternionContinuity();return result;
  }catch{Object.DestroyImmediate(result);throw;}finally{Object.DestroyImmediate(go);}
 }
}

public class CameraAdjustmentWindow:EditorWindow
{
 AnimationClip source,body;GameObject model;
 float reference=1.5f,target=1.5f,scale=1,height,distance=1,yaw,horizontal,time;
 bool playing;float previewEyeHeight=1.5f,previewYaw=180;double last;string message="";
 Material placeholderMaterial;
 Scene previewScene;GameObject root,avatar;Transform pivot,child;Camera view;RenderTexture texture;Animator animator;AnimatorController controller;
 [MenuItem("Tools/Pokahi MMD Camera Tools/カメラ調整・プレビュー")]
 public static void Open(){GetWindow<CameraAdjustmentWindow>("カメラ調整").minSize=new Vector2(620,650);}
 void OnEnable(){EditorApplication.update+=Tick;last=EditorApplication.timeSinceStartup;}
 void OnDisable(){EditorApplication.update-=Tick;Dispose();}
 void Tick(){double now=EditorApplication.timeSinceStartup;if(playing&&source!=null){time=(time+(float)(now-last))%Mathf.Max(.01f,source.length);Repaint();}last=now;}
 void OnGUI(){
  EditorGUILayout.HelpBox("身長比の補正＋映像を見ながらの微調整。元データを変更せず、調整版の.animを別保存します。",MessageType.Info);
  EditorGUI.BeginChangeCheck();source=(AnimationClip)EditorGUILayout.ObjectField("カメラ",source,typeof(AnimationClip),false);body=(AnimationClip)EditorGUILayout.ObjectField("確認用モーション",body,typeof(AnimationClip),false);model=(GameObject)EditorGUILayout.ObjectField("確認用アバター",model,typeof(GameObject),false);
  previewEyeHeight=EditorGUILayout.Slider("プレビュー目線高（m）",previewEyeHeight,.3f,3);previewYaw=EditorGUILayout.Slider("プレビューアバターの向き",previewYaw,-180,180);
  if(EditorGUI.EndChangeCheck()){Dispose();time=0;playing=false;message="";}
  reference=EditorGUILayout.FloatField("元の想定目線高（m）",reference);target=EditorGUILayout.FloatField("合わせる目線高（m）",target);
  if(GUILayout.Button("目線高の比率から倍率を設定"))scale=Mathf.Clamp(target/Mathf.Max(.1f,reference),.1f,5);
  scale=EditorGUILayout.Slider("全体の倍率",scale,.1f,5);height=EditorGUILayout.Slider("上下の補正（m）",height,-2,2);horizontal=EditorGUILayout.Slider("左右の移動（m）",horizontal,-3,3);distance=EditorGUILayout.Slider("注視点からの距離倍率",distance,.25f,3);yaw=EditorGUILayout.Slider("左右に回す角度",yaw,-180,180);
  EditorGUILayout.HelpBox("左右の移動は映像の向きを基準に、＋でカメラを右、－で左へ動かします（被写体は反対側へ移ります）。実際のワールドと同じ目線高を指定し、通常は倍率1から調整してください。プレビュー用の設定は出力カメラやアバターを変更しません。距離倍率はCameraのローカル位置に作用するため、移動がPivotだけに入ったカメラには効きません。",MessageType.None);
  using(new EditorGUI.DisabledScope(source==null||EditorApplication.isPlayingOrWillChangePlaymode)){
   EditorGUILayout.BeginHorizontal();if(GUILayout.Button(playing?"一時停止":"再生")){playing=!playing;last=EditorApplication.timeSinceStartup;}if(GUILayout.Button("補正をリセット")){scale=distance=1;height=yaw=horizontal=0;}EditorGUILayout.EndHorizontal();
   time=EditorGUILayout.Slider("確認する時刻",time,0,source==null?1:source.length);
   if(GUILayout.Button("調整したカメラを別名で保存…"))Save();
  }
  var rect=GUILayoutUtility.GetAspectRect(16f/9f);
  if(source!=null&&!EditorApplication.isPlayingOrWillChangePlaymode&&Event.current.type==EventType.Repaint){try{Render();GUI.DrawTexture(rect,texture,ScaleMode.ScaleToFit,false);}catch(Exception e){message=e.Message;playing=false;}}
  EditorGUILayout.LabelField("プレビューは簡易背景・16:9です。実際のVRChatでの身長・視線・表情はBuild & Testで確認してください。",EditorStyles.wordWrappedLabel);
  if(!string.IsNullOrEmpty(message))EditorGUILayout.HelpBox(message,MessageType.Info);
 }
 void Build(){
  CameraAdjustment.Validate(source);previewScene=EditorSceneManager.NewPreviewScene();
  root=new GameObject("Camera preview"){hideFlags=HideFlags.HideAndDontSave};SceneManager.MoveGameObjectToScene(root,previewScene);root.transform.rotation=Quaternion.identity;
  pivot=new GameObject("Pivot").transform;pivot.SetParent(root.transform,false);child=new GameObject("Camera").transform;child.SetParent(pivot,false);view=child.gameObject.AddComponent<Camera>();view.enabled=false;view.scene=previewScene;view.clearFlags=CameraClearFlags.SolidColor;view.backgroundColor=new Color(.4f,.31f,.55f);view.nearClipPlane=.01f;view.farClipPlane=500;view.aspect=16f/9f;
  texture=new RenderTexture(960,540,24);texture.hideFlags=HideFlags.HideAndDontSave;view.targetTexture=texture;
  var holder=new GameObject("Preview model"){hideFlags=HideFlags.HideAndDontSave};SceneManager.MoveGameObjectToScene(holder,previewScene);holder.SetActive(false);
  if(model!=null){avatar=Object.Instantiate(model,holder.transform);foreach(var script in avatar.GetComponentsInChildren<MonoBehaviour>(true))Object.DestroyImmediate(script);foreach(var c in avatar.GetComponentsInChildren<Camera>(true))Object.DestroyImmediate(c);foreach(var a in avatar.GetComponentsInChildren<AudioSource>(true))Object.DestroyImmediate(a);avatar.transform.localPosition=Vector3.zero;avatar.transform.localRotation=Quaternion.Euler(0,previewYaw,0);
   animator=avatar.GetComponent<Animator>();if(animator!=null&&body!=null){controller=new AnimatorController();controller.AddLayer("Preview");var state=controller.layers[0].stateMachine.AddState("Motion");state.motion=body;state.writeDefaultValues=true;animator.runtimeAnimatorController=controller;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.applyRootMotion=true;}
  }else{avatar=GameObject.CreatePrimitive(PrimitiveType.Capsule);SceneManager.MoveGameObjectToScene(avatar,previewScene);avatar.transform.SetParent(holder.transform);avatar.transform.localPosition=new Vector3(0,.8f,0);avatar.transform.localScale=new Vector3(.45f,previewEyeHeight*.5333333f,.45f);avatar.transform.localPosition=Vector3.up*previewEyeHeight*.5333333f;
   placeholderMaterial=new Material(Shader.Find("Unlit/Color")){hideFlags=HideFlags.HideAndDontSave};placeholderMaterial.color=new Color(.85f,.88f,1);avatar.GetComponent<Renderer>().sharedMaterial=placeholderMaterial;}
  holder.SetActive(true);
  if(animator!=null&&animator.isHuman){animator.Rebind();var eye=animator.GetBoneTransform(HumanBodyBones.LeftEye);if(eye==null)eye=animator.GetBoneTransform(HumanBodyBones.Head);if(eye!=null){float h=eye.position.y-holder.transform.position.y;if(h>.1f)avatar.transform.localScale*=previewEyeHeight/h;}}
  var light=new GameObject("Preview light");SceneManager.MoveGameObjectToScene(light,previewScene);var component=light.AddComponent<Light>();component.type=LightType.Directional;component.intensity=1.3f;light.transform.rotation=Quaternion.Euler(35,-30,0);
 }
 void Render(){
  if(root==null)Build();pivot.localPosition=Vector3.zero;pivot.localRotation=Quaternion.identity;child.localPosition=Vector3.zero;child.localRotation=Quaternion.Euler(0,180,0);view.fieldOfView=60;
  source.SampleAnimation(root,time);CameraAdjustment.Adjust(pivot,child,scale,height,distance,yaw,horizontal);
  if(animator!=null&&body!=null){avatar.transform.localPosition=Vector3.zero;avatar.transform.localRotation=Quaternion.Euler(0,previewYaw,0);animator.Play("Motion",0,Mathf.Clamp01(time/Mathf.Max(.01f,body.length)));animator.Update(0);}
  view.Render();
 }
 void Save(){AnimationClip output=null;try{
  CameraAdjustment.Validate(source);var path=EditorUtility.SaveFilePanelInProject("調整したカメラを保存",source.name+"_Adjusted","anim","原本は残します。");if(string.IsNullOrEmpty(path))return;
  output=CameraAdjustment.Create(source,scale,height,distance,yaw,horizontal);path=AssetDatabase.GenerateUniqueAssetPath(path);AssetDatabase.CreateAsset(output,path);AssetDatabase.SaveAssets();EditorGUIUtility.PingObject(output);message="保存しました："+path+"。使用先のAnimatorや曲登録ツールに出力ファイルを指定してください。";
 }catch(Exception e){message=e.Message;if(output!=null&&!AssetDatabase.Contains(output))Object.DestroyImmediate(output);}}
 void Dispose(){if(placeholderMaterial!=null){Object.DestroyImmediate(placeholderMaterial);placeholderMaterial=null;}if(texture!=null){texture.Release();Object.DestroyImmediate(texture);}if(previewScene.IsValid())EditorSceneManager.ClosePreviewScene(previewScene);if(controller!=null)Object.DestroyImmediate(controller);root=null;avatar=null;animator=null;view=null;texture=null;}
}


}
