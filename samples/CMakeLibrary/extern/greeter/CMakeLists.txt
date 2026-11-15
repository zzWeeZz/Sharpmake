cmake_minimum_required(VERSION 3.14)
project(Greeter VERSION 1.0.0 LANGUAGES CXX)

option(GREETER_SHOUT "Greet in upper case" OFF)

file(GLOB GREETER_SOURCES CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp)

add_library(greeter STATIC ${GREETER_SOURCES})
add_library(greeter::greeter ALIAS greeter)

target_include_directories(greeter
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        src)

target_compile_definitions(greeter
    PUBLIC GREETER_VERSION="${PROJECT_VERSION}"
    PRIVATE GREETER_BUILDING)

if(GREETER_SHOUT)
    target_compile_definitions(greeter PRIVATE GREETER_SHOUT=1)
endif()
