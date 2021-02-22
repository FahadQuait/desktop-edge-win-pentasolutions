package service

import (
	"encoding/json"
	"fmt"

	"github.com/openziti/desktop-edge-win/service/ziti-tunnel/dto"
)

func GetIdentityFromRTS(args string) dto.Response {
	message := fmt.Sprintf("Listing Identities - %s", args)

	identities, err := json.Marshal(rts.state.Identities)
	if err != nil {
		log.Error(err)
		return dto.Response{Message: message, Code: ERROR, Error: "Could not fetch Identities from Runtime", Payload: nil}
	}
	var identitiesMapList []map[string]interface{}
	json.Unmarshal(identities, &identitiesMapList)

	for _, identitiesMap := range identitiesMapList {
		for field, _ := range identitiesMap {
			if field != "Name" && field != "FingerPrint" && field != "Active" && field != "ControllerVersion" && field != "Status" && field != "Config" {
				delete(identitiesMap, field)
			}
		}
	}

	identitiesBytes, mapErr := json.Marshal(identitiesMapList)
	if mapErr != nil {
		log.Error(mapErr)
		return dto.Response{Message: message, Code: ERROR, Error: "Could not transform Identities to json", Payload: nil}
	}

	identitiesStr := string(identitiesBytes)
	log.Infof("RTS %s", identitiesStr)

	return dto.Response{Message: message, Code: SUCCESS, Error: "", Payload: identitiesStr}
}
