﻿/// OSVR-Unity Connection
///
/// http://sensics.com/osvr
///
/// <copyright>
/// Copyright 2015 Sensics, Inc.
///
/// Licensed under the Apache License, Version 2.0 (the "License");
/// you may not use this file except in compliance with the License.
/// You may obtain a copy of the License at
///
///     http://www.apache.org/licenses/LICENSE-2.0
///
/// Unless required by applicable law or agreed to in writing, software
/// distributed under the License is distributed on an "AS IS" BASIS,
/// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
/// See the License for the specific language governing permissions and
/// limitations under the License.
/// </copyright>
/// <summary>
/// Author: Greg Aring
/// Email: greg@sensics.com
/// </summary>
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using System;

namespace OSVR
{
    namespace Unity
    {
        //*This class is responsible for creating stereo rendering in a scene, and updating viewing parameters
        // throughout a scene's lifecycle. 
        // The number of viewers, eyes, and surfaces, as well as viewports, projection matrices,and distortion 
        // paramerters are obtained from OSVR via ClientKit.
        // 
        // DisplayController creates VRViewers and VREyes as children. Although VRViewers and VREyes are siblings
        // in the scene hierarchy, conceptually VREyes are indeed children of VRViewers. The reason they are siblings
        // in the Unity scene is because GetViewerEyePose(...) returns a pose relative to world space, not head space.
        //
        // In this implementation, we are assuming that there is exactly one viewer and one surface per eye.
        //*/
        public class DisplayController : MonoBehaviour
        {

            public const uint NUM_VIEWERS = 1;
            private const int TARGET_FRAME_RATE = 60; //@todo get from OSVR

            private ClientKit _clientKit;
            private OSVR.ClientKit.DisplayConfig _displayConfig;
            private VRViewer[] _viewers;
            private uint _viewerCount;
            private bool _displayConfigInitialized = false;
            private bool _checkDisplayStartup = false;
            private uint _totalDisplayWidth;
            private uint _totalSurfaceHeight;

            //variables for controlling use of osvrUnityRenderingPlugin.dll which enables DirectMode
            private OsvrRenderManager _renderManager;
            private bool _useRenderManager = false; //requires Unity 5.2+ and RenderManager configured osvr server
            public bool UseRenderManager { get { return _useRenderManager; } }
 

            public OSVR.ClientKit.DisplayConfig DisplayConfig
            {
                get { return _displayConfig; }
                set { _displayConfig = value; }
            }
            public VRViewer[] Viewers { get { return _viewers; } }
            public uint ViewerCount { get { return _viewerCount; } }
            public OsvrRenderManager RenderManager { get { return _renderManager; } }

            public uint TotalDisplayWidth
            {
                get
                {
                    return _totalDisplayWidth;
                }

                set
                {
                    _totalDisplayWidth = value;
                }
            }

            public uint TotalDisplayHeight
            {
                get
                {
                    return _totalSurfaceHeight;
                }

                set
                {
                    _totalSurfaceHeight = value;
                }
            }

            void Awake()
            {
                _clientKit = FindObjectOfType<ClientKit>();
                if (_clientKit == null)
                {
                    Debug.LogError("DisplayController requires a ClientKit object in the scene.");
                }
               
                SetupApplicationSettings();

            }
  

            void SetupApplicationSettings()
            {
                //VR should never timeout the screen:
                Screen.sleepTimeout = SleepTimeout.NeverSleep;

                //Set the framerate
                //@todo get this value from OSVR, not a const value
                //Performance note: Developers should try setting Time.fixedTimestep to 1/Application.targetFrameRate
                //Application.targetFrameRate = TARGET_FRAME_RATE;
            }

            // Setup RenderManager for DirectMode or non-DirectMode rendering.
            // Checks to make sure Unity version and Graphics API are supported, 
            // and that a RenderManager config file is being used.
            void SetupRenderManager()
            {
                //check if we are configured to use RenderManager or not
                string renderManagerPath = _clientKit.context.getStringParameter("/renderManagerConfig");
                _useRenderManager = !(renderManagerPath == null || renderManagerPath.Equals(""));
                if (_useRenderManager)
                {
                    //found a RenderManager config
                    _renderManager = GameObject.FindObjectOfType<OsvrRenderManager>();
                    if (_renderManager == null)
                    {
                        //add a RenderManager component
                        _renderManager = gameObject.AddComponent<OsvrRenderManager>();
                    }

                    //check to make sure Unity version and Graphics API are supported
                    bool supportsRenderManager = _renderManager.IsRenderManagerSupported();
                    _useRenderManager = supportsRenderManager;
                    if (!_useRenderManager)
                    {
                        Debug.LogError("RenderManager config found but RenderManager is not supported.");
                        Destroy(_renderManager);
                    }
                    else
                    {
                        // attempt to create a RenderManager in the plugin                                              
                        int result = _renderManager.InitRenderManager();
                        if (result != 0)
                        {
                            Debug.LogError("Failed to create RenderManager.");
                            _useRenderManager = false;
                        }
                    }
                }
                else
                {
                    Debug.Log("RenderManager config not detected. Using normal Unity rendering path.");
                }
            }

            // Get a DisplayConfig object from the server via ClientKit.
            // Setup stereo rendering with DisplayConfig data.
            void SetupDisplay()
            {
                //get the DisplayConfig object from ClientKit
                if (_clientKit.context == null)
                {
                    Debug.LogError("ClientContext is null. Can't setup display.");
                    return;
                }
                _displayConfig = _clientKit.context.GetDisplayConfig();
                if (_displayConfig == null)
                {
                    return;
                }
                _displayConfigInitialized = true;

                SetupRenderManager();

                //get the number of viewers, bail if there isn't exactly one viewer for now
                _viewerCount = _displayConfig.GetNumViewers();
                if (_viewerCount != 1)
                {
                    Debug.LogError(_viewerCount + " viewers found, but this implementation requires exactly one viewer.");
                    return;
                }

                //Set Unity player resolution
                SetResolution();

                //create scene objects 
                CreateHeadAndEyes();
            }

            //Set Resolution of the Unity game window based on total surface width
            private void SetResolution()
            {
                TotalDisplayWidth = 0; //add up the width of each eye
                TotalDisplayHeight = 0; //don't add up heights

                int numDisplayInputs = DisplayConfig.GetNumDisplayInputs();
                //for each display
                for (int i = 0; i < numDisplayInputs; i++)
                {
                    OSVR.ClientKit.DisplayDimensions surfaceDisplayDimensions = DisplayConfig.GetDisplayDimensions((byte)i);

                    TotalDisplayWidth += (uint)surfaceDisplayDimensions.Width; //add up the width of each eye
                    TotalDisplayHeight = (uint)surfaceDisplayDimensions.Height; //store the height -- this shouldn't change
                }

                //Set the resolution. Don't force fullscreen if we have multiple display inputs
                //We only need to do this if we aren't using RenderManager, because it adjusts the window size for us
                //@todo figure out why this causes problems with direct mode, perhaps overfill factor?
                if(numDisplayInputs > 1 && !UseRenderManager)
                {
                    Screen.SetResolution((int)TotalDisplayWidth, (int)TotalDisplayHeight, false);
                }                             
                
            }

            // Creates a head and eyes as configured in clientKit
            // Viewers and Eyes are siblings, children of DisplayController
            // Each eye has one child Surface which has a camera
            private void CreateHeadAndEyes()
            {
                /* ASSUME ONE VIEWER */
                // Create VRViewers, only one in this implementation
                _viewerCount = (uint)_displayConfig.GetNumViewers();
                if (_viewerCount != NUM_VIEWERS)
                {
                    Debug.LogError(_viewerCount + " viewers detected. This implementation supports exactly one viewer.");
                    return;
                }
                _viewers = new VRViewer[_viewerCount];

                uint viewerIndex = 0;

                //Check if there are already VRViewers in the scene.
                //If so, create eyes for them.
                VRViewer[] viewersInScene = FindObjectsOfType<VRViewer>();
                if (viewersInScene != null && viewersInScene.Length > 0)
                {
                    for(viewerIndex = 0; viewerIndex < viewersInScene.Length; viewerIndex++)
                    {
                        VRViewer viewer = viewersInScene[viewerIndex];
                        // get the VRViewer gameobject
                        GameObject vrViewer = viewer.gameObject;
                        vrViewer.name = "VRViewer" + viewerIndex; //change its name to VRViewer0
                        //@todo optionally add components                      
                        if (vrViewer.GetComponent<AudioListener>() == null)
                        {
                            vrViewer.AddComponent<AudioListener>(); //add an audio listener
                        }
                        viewer.DisplayController = this; //pass DisplayController to Viewers  
                        viewer.ViewerIndex = viewerIndex; //set the viewer's index                         
                        vrViewer.transform.parent = this.transform; //child of DisplayController
                        vrViewer.transform.localPosition = Vector3.zero;
                        _viewers[viewerIndex] = viewer;
                        if( viewerIndex == 0)
                        {
                            vrViewer.tag = "MainCamera"; //set the MainCamera tag for the first Viewer
                        }

                        // create Viewer's VREyes
                        uint eyeCount = (uint)_displayConfig.GetNumEyesForViewer(viewerIndex); //get the number of eyes for this viewer
                        viewer.CreateEyes(eyeCount);
                    }         
                }

                // loop through viewers because at some point we could support multiple viewers
                // but this implementation currently supports exactly one
                for (; viewerIndex < _viewerCount; viewerIndex++)
                {
                    // create a VRViewer
                    GameObject vrViewer = new GameObject("VRViewer" + viewerIndex);
                    if(vrViewer.GetComponent<AudioListener>() == null)
                    {
                        vrViewer.AddComponent<AudioListener>(); //add an audio listener
                    }
                    
                    VRViewer vrViewerComponent = vrViewer.AddComponent<VRViewer>();
                    vrViewerComponent.DisplayController = this; //pass DisplayController to Viewers  
                    vrViewerComponent.ViewerIndex = viewerIndex; //set the viewer's index                         
                    vrViewer.transform.parent = this.transform; //child of DisplayController
                    vrViewer.transform.localPosition = Vector3.zero;
                    _viewers[viewerIndex] = vrViewerComponent;
                    vrViewer.tag = "MainCamera";

                    // create Viewer's VREyes
                    uint eyeCount = (uint)_displayConfig.GetNumEyesForViewer(viewerIndex); //get the number of eyes for this viewer
                    vrViewerComponent.CreateEyes(eyeCount);
                }
            }

            void Update()
            {
                // sometimes it takes a few frames to get a DisplayConfig from ClientKit
                // keep trying until we have initialized
                if (!_displayConfigInitialized)
                {
                    SetupDisplay();
                }
                if (!_checkDisplayStartup && _displayConfigInitialized)
                {
                    _checkDisplayStartup = DisplayConfig.CheckDisplayStartup();
                }
            }

            //helper method for updating the client context
            public void UpdateClient()
            {
                _clientKit.context.update();
            }

            public bool CheckDisplayStartup()
            {
                return _displayConfigInitialized && DisplayConfig.CheckDisplayStartup();
            }

            internal void ExitRenderManager()
            {
                RenderManager.ExitRenderManager();
            }

        }
    }
}
