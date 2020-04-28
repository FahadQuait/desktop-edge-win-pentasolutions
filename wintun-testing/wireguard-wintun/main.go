package main

import (
	"bufio"
	"fmt"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
	"golang.zx2c4.com/wireguard/device"
	"golang.zx2c4.com/wireguard/ipc"
	"golang.zx2c4.com/wireguard/tun"
	"golang.zx2c4.com/wireguard/windows/conf"
	"golang.zx2c4.com/wireguard/windows/elevate"
	"golang.zx2c4.com/wireguard/windows/services"
	"golang.zx2c4.com/wireguard/windows/version"
	"log"
	"os"
	"strings"
	"wintun-testing/wireguard-wintun/wireguard"
)

func main() {
	var r <-chan svc.ChangeRequest
	var changes chan<- svc.Status
	confPath := "c:\\git\\github\\ziti-windows-uwp\\wintun-testing\\simple.conf"
	if envPath := os.Getenv("WIREGUARD_TESTING_CONF"); envPath != "" {
		confPath = envPath
	}
	interfaceName := "ZitiTUN"

	serviceError := services.ErrorSuccess
	conf, err := conf.LoadFromPath(confPath)
	if err != nil {
		panic(err)
	}
	conf.DeduplicateNetworkEntries()

	logPrefix := fmt.Sprintf("[%s] ", conf.Name)
	log.SetPrefix(logPrefix)

	log.Println("Starting", version.UserAgent())

	if m, err := mgr.Connect(); err == nil {
		if lockStatus, err := m.LockStatus(); err == nil && lockStatus.IsLocked {
			/* If we don't do this, then the Wintun installation will block forever, because
			 * installing a Wintun device starts a service too. Apparently at boot time, Windows
			 * 8.1 locks the SCM for each service start, creating a deadlock if we don't announce
			 * that we're running before starting additional services.
			 */
			log.Printf("SCM locked for %v by %s, marking service as started", lockStatus.Age, lockStatus.Owner)
			changes <- svc.Status{State: svc.Running}
		}
		m.Disconnect()
	}

	if serviceError != services.ErrorSuccess {
		panic(serviceError)
	}

	var watcher *wireguard.InterfaceWatcher
	log.Println("Watching network interfaces")
	watcher, err = wireguard.WatchInterface()
	if err != nil {
		panic(err)
	}

	log.Println("Resolving DNS names")
	uapiConf, err := conf.ToUAPI()
	if err != nil {
		panic(err)
	}

	log.Println("Creating Wintun interface")
	tunDevice, err := tun.CreateTUN(interfaceName, 0)
	if err != nil {
		panic(err)
	}
	defer tunDevice.Close()

	nativeTun := tunDevice.(*tun.NativeTun)
	wintunVersion, ndisVersion, err := nativeTun.Version()
	if err != nil {
		panic(err)
	} else {
		log.Printf("Using Wintun/%s (NDIS %s)", wintunVersion, ndisVersion)
	}

	log.Println("Enabling firewall rules")
	err = wireguard.EnableFirewall(conf, nativeTun)
	if err != nil {
		panic(err)
	}

	log.Println("Dropping privileges")
	err = elevate.DropAllPrivileges(true)
	if err != nil {
		panic(err)
	}

	log.Println("Creating interface instance")
	dev := device.NewDevice(tunDevice, device.NewLogger(
		device.LogLevelDebug,
		fmt.Sprintf("(%s) ", interfaceName),
	))

	log.Println("Setting interface configuration")
	uapi, err := ipc.UAPIListen(conf.Name)
	if err != nil {
		panic(err)
	}
	ipcErr := dev.IpcSetOperation(bufio.NewReader(strings.NewReader(uapiConf)))
	if ipcErr != nil {
		panic(err)
	}

	log.Println("Bringing peers up")
	dev.Up()

	watcher.Configure(dev, conf, nativeTun)

	log.Println("Listening for UAPI requests")
	go func() {
		for {
			conn, err := uapi.Accept()
			if err != nil {
				continue
			}
			go dev.IpcHandle(conn)
		}
	}()

	changes <- svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	log.Println("Startup complete")

	for {
		select {
		case c := <-r:
			switch c.Cmd {
			case svc.Stop, svc.Shutdown:
				return
			case svc.Interrogate:
				changes <- c.CurrentStatus
			default:
				log.Printf("Unexpected service control request #%d\n", c)
			}
		case <-dev.Wait():
			return
		case e := <-watcher.Errors:
			serviceError, err = e.ServiceError, e.Err
			return
		}
	}
}
