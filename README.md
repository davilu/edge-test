# edge-test

## Build and push to your container registry

```bash
az acr login --name [YOUR_CONTAINER_REGISTRY_NAME]

dotnet clean
dotnet build -c release
dotnet publish -f netcoreapp3.1 -o publish 

docker build^
 --build-arg ARG_IOTHUB_HOST=[YOUR_HUB_URL]^
 --build-arg ARG_IOTHUB_OWNER_SHARED_ACCESS_KEY=[YOUR_HUB_ACCESS_KEY]^
 --build-arg ARG_EDGEHUB_HOST=[YOUR_EDGE_HUB_HOST]^
 --build-arg ARG_EDGE_DEVICE_ID=[YOUR_EDGE_HUB_DEVICE_ID]^
 --build-arg ARG_LEAF_DEVICE_ID_PREFIX=[YOUR_LEAF_DEVICE_ID_PREFIX]^
 --build-arg ARG_USE_NATIVE_MQTT_CLIENT=[ON|OFF]^
 --build-arg ARG_PUBLISH_TOPICS=[EXTRAL_TOPICS_TO_PUBLISH]^
 --build-arg ARG_SUBSCRIPTION_TOPICS=[EXTRAL_TOPIC_TO_SUBSCRIBES]^
 --build-arg ARG_DEBUG_LOG_ON=[ON|OFF]^
 -t [YOUR_CONTAINER_URL]/[YOUR_IMAGE_NAME]^
 -f publish/docker/linux/amd64/Dockerfile^
 --no-cache^
 publish/ 

docker push [YOUR_CONTAINER_URL]/edgetestappl4
```

* You need multi-images for different layers, just run the script multi-times with different name and make sure leaf device prefixes are different.
* Suggest image name: edge-test-app-l4, edge-test-app-l35 and edge-test-app-l3
* Suggest leaf device prefixes: edge-test-leaf-l4, edge-test-leaf-l35, edge-test-leaf-l3
* Extral publish and subscrition topics only works with native MQTT broker on. Multi topics supported with `;` separated. It could be used for testing MQTT broker with right policy and route setup. You could even test publishing message to upper layer. 
* C2D, method and twin are subscribed even with native MQTT broker.
* If edge hub host is set, native broker will connect to edge hub; otherwise to iot hub instead.
* Default debug log and native mqtt client is off.

## Go to your hub and add module with the image created and you can see it will run on your VM.