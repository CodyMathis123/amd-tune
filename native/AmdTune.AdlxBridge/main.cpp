// Auditable AMD ADLX 2.0 bridge. No network, persistence, registry or fan changes.
// Must be compiled locally against AMD's official SDK. One discrete AMD GPU only.
#include <windows.h>
#include <iostream>
#include <sstream>
#include <iomanip>
#include <string>
#include <stdexcept>
#include "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.h"
#include "SDK/Include/IGPUTuning.h"
#include "SDK/Include/IGPUManualGFXTuning.h"
#include "SDK/Include/IGPUManualVRAMTuning.h"
#include "SDK/Include/IPerformanceMonitoring.h"
using namespace adlx;

static std::string Escape(const char* src) {
    std::string out;
    if (!src) return "Unknown AMD GPU";
    while (*src) { if (*src == '"' || *src == '\\') out += '\\'; if ((unsigned char)*src >= 32) out += *src; ++src; }
    return out;
}
static std::string IsoUtc() {
    SYSTEMTIME t; GetSystemTime(&t);
    std::ostringstream s; s << std::setfill('0') << std::setw(4) << t.wYear << "-"
        << std::setw(2) << t.wMonth << "-" << std::setw(2) << t.wDay << "T"
        << std::setw(2) << t.wHour << ":" << std::setw(2) << t.wMinute << ":"
        << std::setw(2) << t.wSecond << "." << std::setw(3) << t.wMilliseconds << "Z";
    return s.str();
}
struct Settings { adlx_int uv = 0; adlx_int memory = 0; ADLX_MEMORYTIMING_DESCRIPTION timing = MEMORYTIMING_DEFAULT; };
static Settings Read(IADLXManualGraphicsTuning2Ptr& g, IADLXManualVRAMTuning2Ptr& v, bool timing) {
    Settings s;
    if (ADLX_FAILED(g->GetGPUVoltage(&s.uv)) || ADLX_FAILED(v->GetMaxVRAMFrequency(&s.memory)))
        throw std::runtime_error("Driver tuning readback unsupported.");
    if (timing && ADLX_FAILED(v->GetMemoryTimingDescription(&s.timing)))
        throw std::runtime_error("Driver memory timing readback unsupported.");
    return s;
}
static void Write(IADLXManualGraphicsTuning2Ptr& g, IADLXManualVRAMTuning2Ptr& v, bool timing, const Settings& s) {
    if (ADLX_FAILED(g->SetGPUVoltage(s.uv))) throw std::runtime_error("Voltage setting rejected.");
    if (ADLX_FAILED(v->SetMaxVRAMFrequency(s.memory))) throw std::runtime_error("Memory frequency rejected.");
    if (timing && ADLX_FAILED(v->SetMemoryTimingDescription(s.timing))) throw std::runtime_error("Memory timing rejected.");
    const Settings actual = Read(g,v,timing);
    if (actual.uv != s.uv || actual.memory != s.memory || (timing && actual.timing != s.timing))
        throw std::runtime_error("Actual driver settings differ from requested settings.");
}
static int Execute(int argc,char** argv) {
    if (argc < 2) throw std::invalid_argument("Expected discover/read/telemetry/apply.");
    ADLXHelper helper;
    if (ADLX_FAILED(helper.Initialize())) throw std::runtime_error("ADLX initialize failed.");
    auto system = helper.GetSystemServices();
    if (!system) throw std::runtime_error("ADLX system services unavailable.");
    IADLXGPUListPtr list;
    if (ADLX_FAILED(system->GetGPUs(&list)) || list == nullptr) throw std::runtime_error("Cannot enumerate GPUs.");
    IADLXGPUPtr gpu;
    int count=0;
    for (adlx_uint i=0;i<list->Size();i++) {
        IADLXGPUPtr candidate;
        if (ADLX_FAILED(list->At(i,&candidate)) || candidate == nullptr) continue;
        ADLX_GPU_TYPE type=GPUTYPE_UNDEFINED;
        if (ADLX_SUCCEEDED(candidate->Type(&type)) && type == GPUTYPE_DISCRETE) { gpu=candidate; count++; }
    }
    if (count != 1 || gpu == nullptr) throw std::runtime_error("Exactly one AMD discrete GPU required.");
    const char* nameRaw=nullptr; gpu->Name(&nameRaw);
    const std::string name=Escape(nameRaw);
    IADLXGPUTuningServicesPtr tuner;
    if (ADLX_FAILED(system->GetGPUTuningServices(&tuner)) || tuner == nullptr)
        throw std::runtime_error("ADLX tuning unavailable.");
    IADLXInterfacePtr gbase,vbase;
    if (ADLX_FAILED(tuner->GetManualGFXTuning(gpu,&gbase)) || gbase == nullptr ||
        ADLX_FAILED(tuner->GetManualVRAMTuning(gpu,&vbase)) || vbase == nullptr)
        throw std::runtime_error("Driver manual tuning interfaces unavailable.");
    IADLXManualGraphicsTuning2Ptr gfx(gbase);
    IADLXManualVRAMTuning2Ptr vram(vbase);
    if (gfx == nullptr || vram == nullptr) throw std::runtime_error("ADLX manual tuning version 2 unavailable.");
    adlx_bool timingSupportedRaw=false;
    const bool timingSupported=ADLX_SUCCEEDED(vram->IsSupportedMemoryTiming(&timingSupportedRaw)) && timingSupportedRaw;
    ADLX_IntRange ur={0,0,0}, mr={0,0,0};
    const bool ranges=ADLX_SUCCEEDED(gfx->GetGPUVoltageRange(&ur)) &&
        ADLX_SUCCEEDED(vram->GetMaxVRAMFrequencyRange(&mr)) &&
        ur.maxValue > ur.minValue && mr.maxValue > mr.minValue;
    std::string cmd(argv[1]);
    if (cmd=="discover" && argc==2) {
        bool readable=true;
        try { (void)Read(gfx,vram,timingSupported); } catch (...) { readable=false; }
        std::cout << "{\"deviceId\":\"adlx:single-discrete\",\"name\":\"" << name
            << "\",\"backend\":\"AMD ADLX 2.0\",\"isSimulation\":false,\"canReadSettings\":"
            << (readable?"true":"false") << ",\"canWriteSettings\":" << (readable && ranges?"true":"false")
            << ",\"voltageOffsetMv\":";
        if (ranges) std::cout << "{\"min\":"<<ur.minValue<<",\"max\":"<<ur.maxValue<<",\"step\":"<<(ur.step>0?ur.step:1)<<"}";
        else std::cout<<"null";
        std::cout << ",\"memoryClockMhz\":";
        if (ranges) std::cout << "{\"min\":"<<mr.minValue<<",\"max\":"<<mr.maxValue<<",\"step\":"<<(mr.step>0?mr.step:1)<<"}";
        else std::cout<<"null";
        std::cout << ",\"canSetMemoryTiming\":"<<(timingSupported?"true":"false")
                  << ",\"warnings\":[\"No writes unless independent recovery is armed.\"]}"<<std::endl;
    } else if (cmd=="read" && argc==2) {
        const Settings s=Read(gfx,vram,timingSupported);
        std::cout << "{\"voltageOffsetMv\":"<<s.uv<<",\"memoryClockMhz\":"<<s.memory
                  <<",\"memoryTiming\":"<<(s.timing==MEMORYTIMING_FAST_TIMING?1:0)<<"}"<<std::endl;
    } else if (cmd=="telemetry" && argc==2) {
        IADLXPerformanceMonitoringServicesPtr monitor;
        if (ADLX_FAILED(system->GetPerformanceMonitoringServices(&monitor)) || monitor==nullptr)
            throw std::runtime_error("Monitoring service unavailable.");
        IADLXGPUMetricsPtr sample;
        if (ADLX_FAILED(monitor->GetCurrentGPUMetrics(gpu,&sample)) || sample==nullptr)
            throw std::runtime_error("Cannot sample GPU telemetry.");
        adlx_double hotspot=0,power=0; adlx_int core=0,memory=0;
        if (ADLX_FAILED(sample->GPUHotspotTemperature(&hotspot)) || hotspot<=0 ||
            ADLX_FAILED(sample->GPUTotalBoardPower(&power)) ||
            ADLX_FAILED(sample->GPUClockSpeed(&core)) ||
            ADLX_FAILED(sample->GPUVRAMClockSpeed(&memory)))
            throw std::runtime_error("Required live GPU metrics unavailable.");
        adlx_double usage=0;
        const bool hasUsage=ADLX_SUCCEEDED(sample->GPUUsage(&usage)) && usage>=0 && usage<=100;
        std::cout<<"{\"at\":\""<<IsoUtc()<<"\",\"hotspotC\":"<<hotspot<<",\"boardPowerW\":"
                 <<power<<",\"coreClockMhz\":"<<core<<",\"memoryClockMhz\":"<<memory
                 <<",\"utilizationPercent\":";
        if (hasUsage) std::cout<<usage;
        else std::cout<<"null";
        std::cout<<"}"<<std::endl;
    } else if (cmd=="apply" && argc==5) {
        if (!ranges) throw std::runtime_error("Driver limits unknown; refusing write.");
        const int uv=std::stoi(argv[2]), clock=std::stoi(argv[3]);
        const std::string timing(argv[4]);
        if (uv<ur.minValue || uv>ur.maxValue || clock<mr.minValue || clock>mr.maxValue)
            throw std::runtime_error("Requested tuning outside driver limits.");
        Settings desired; desired.uv=uv; desired.memory=clock;
        if (timing=="Fast") desired.timing=MEMORYTIMING_FAST_TIMING;
        else if (timing=="Default") desired.timing=MEMORYTIMING_DEFAULT;
        else throw std::invalid_argument("Unsupported timing value.");
        if (!timingSupported && desired.timing != MEMORYTIMING_DEFAULT)
            throw std::runtime_error("Driver cannot set Fast Timing.");
        const Settings baseline=Read(gfx,vram,timingSupported);
        try { Write(gfx,vram,timingSupported,desired); }
        catch (const std::exception& ex) {
            std::string originalError=ex.what();
            try { Write(gfx,vram,timingSupported,baseline); }
            catch (const std::exception& rollbackError) {
                throw std::runtime_error("WRITE AND ROLLBACK FAILED: "+originalError+"; "+rollbackError.what());
            }
            throw std::runtime_error("Write failed; prior settings restored: "+originalError);
        }
        std::cout<<"{\"ok\":true}"<<std::endl;
    } else throw std::invalid_argument("Unknown command or arguments.");
    return 0;
}
int main(int argc,char**argv) {
    try { return Execute(argc,argv); }
    catch (const std::exception& ex) {
        std::cerr<<ex.what()<<std::endl;
        std::cout<<"{\"error\":\""<<Escape(ex.what())<<"\"}"<<std::endl;
        return 1;
    }
}
