/*
 * Copyright (C) 2026 Niratan contributors
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */
package mextensionserver.impl;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FieldInsnNode;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.TypeInsnNode;
import org.objectweb.asm.tree.VarInsnNode;

import java.io.IOException;
import java.net.URI;
import java.nio.file.FileSystem;
import java.nio.file.FileSystems;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Repairs a specific invalid JVM pattern produced by dex2jar from R8 output.
 */
final class R8ConstructorFixer {
    private R8ConstructorFixer() {
    }

    static void patch(Path jarPath) throws IOException {
        URI uri = URI.create("jar:" + jarPath.toUri());
        try (FileSystem jar = FileSystems.newFileSystem(
            uri,
            Map.of())) {
            Map<Path, ClassNode> classes = readClasses(jar);
            Map<String, ClassNode> classesByName = new HashMap<>();
            Set<String> availableTypes = new HashSet<>();
            for (ClassNode classNode : classes.values()) {
                availableTypes.add(classNode.name);
                classesByName.put(classNode.name, classNode);
            }

            Map<String, Set<String>> constructorTargets =
                new HashMap<>();
            for (ClassNode classNode : classes.values()) {
                for (MethodNode method : classNode.methods) {
                    repairSuperclassAllocations(
                        classNode,
                        method,
                        constructorTargets);
                    repairAllocations(
                        method,
                        classesByName,
                        availableTypes,
                        constructorTargets);
                }
            }
            for (Map.Entry<String, Set<String>> target :
                constructorTargets.entrySet()) {
                ClassNode classNode = classesByName.get(
                    target.getKey());
                if (classNode == null) {
                    continue;
                }
                for (String descriptor : target.getValue()) {
                    ensureForwardingConstructor(
                        classNode,
                        descriptor);
                }
            }
            writeClasses(classes);
        }
    }

    private static void repairSuperclassAllocations(
        ClassNode classNode,
        MethodNode method,
        Map<String, Set<String>> constructorTargets) {
        if (!"<clinit>".equals(method.name)
            || classNode.superName == null
            || "java/lang/Object".equals(classNode.superName)) {
            return;
        }
        List<AbstractInsnNode> instructions =
            realInstructions(method);
        for (int index = 0; index < instructions.size(); index++) {
            AbstractInsnNode instruction = instructions.get(index);
            if (!(instruction instanceof TypeInsnNode allocation)
                || allocation.getOpcode() != Opcodes.NEW
                || !classNode.superName.equals(allocation.desc)) {
                continue;
            }
            for (int constructorIndex = index + 1;
                constructorIndex <
                    Math.min(instructions.size(), index + 8);
                constructorIndex++) {
                AbstractInsnNode candidate =
                    instructions.get(constructorIndex);
                if (candidate instanceof MethodInsnNode constructor
                    && constructor.getOpcode() ==
                        Opcodes.INVOKESPECIAL
                    && classNode.superName.equals(
                        constructor.owner)
                    && "<init>".equals(constructor.name)
                    && (writesStaticSelfField(
                            instructions,
                            constructorIndex + 1,
                            classNode.name)
                        || ("java/lang/Enum".equals(
                                classNode.superName)
                            && "(Ljava/lang/String;I)V".equals(
                                constructor.desc)))) {
                    allocation.desc = classNode.name;
                    constructor.owner = classNode.name;
                    addConstructorTarget(
                        constructorTargets,
                        classNode.name,
                        constructor.desc);
                    break;
                }
            }
        }
    }

    private static boolean writesStaticSelfField(
        List<AbstractInsnNode> instructions,
        int index,
        String className) {
        if (index >= instructions.size()
            || !(instructions.get(index) instanceof
                FieldInsnNode field)
            || field.getOpcode() != Opcodes.PUTSTATIC) {
            return false;
        }
        Type type = Type.getType(field.desc);
        return type.getSort() == Type.OBJECT
            && className.equals(type.getInternalName());
    }

    private static Map<Path, ClassNode> readClasses(
        FileSystem jar) throws IOException {
        Map<Path, ClassNode> classes = new HashMap<>();
        try (var paths = Files.walk(jar.getPath("/"))) {
            for (Path path : paths
                .filter(candidate ->
                    !Files.isDirectory(candidate) &&
                    candidate.toString().endsWith(".class"))
                .toList()) {
                ClassNode node = new ClassNode(Opcodes.ASM9);
                new ClassReader(Files.readAllBytes(path))
                    .accept(node, 0);
                classes.put(path, node);
            }
        }
        return classes;
    }

    private static void repairAllocations(
        MethodNode method,
        Map<String, ClassNode> classesByName,
        Set<String> availableTypes,
        Map<String, Set<String>> constructorTargets) {
        List<AbstractInsnNode> instructions =
            realInstructions(method);
        for (int index = 0; index + 3 < instructions.size(); index++) {
            AbstractInsnNode first = instructions.get(index);
            AbstractInsnNode second = instructions.get(index + 1);
            AbstractInsnNode third = instructions.get(index + 2);
            if (!(first instanceof TypeInsnNode allocation)
                || allocation.getOpcode() != Opcodes.NEW
                || !"java/lang/Object".equals(allocation.desc)
                || second.getOpcode() != Opcodes.DUP
                || !(third instanceof MethodInsnNode constructor)
                || constructor.getOpcode() != Opcodes.INVOKESPECIAL
                || !"java/lang/Object".equals(constructor.owner)
                || !"<init>".equals(constructor.name)
                || !"()V".equals(constructor.desc)) {
                continue;
            }

            String target = destinationType(
                instructions,
                index + 3,
                classesByName,
                availableTypes);
            if (target == null) {
                continue;
            }

            allocation.desc = target;
            constructor.owner = target;
            addConstructorTarget(
                constructorTargets,
                target,
                "()V");
        }
    }

    private static String destinationType(
        List<AbstractInsnNode> instructions,
        int index,
        Map<String, ClassNode> classesByName,
        Set<String> availableTypes) {
        AbstractInsnNode direct = instructions.get(index);
        if (direct instanceof FieldInsnNode field
            && isFieldWrite(field)) {
            return fieldType(field, availableTypes);
        }
        if (direct instanceof MethodInsnNode invocation) {
            Type[] arguments = Type.getArgumentTypes(
                invocation.desc);
            if (arguments.length > 0) {
                Type argument = arguments[arguments.length - 1];
                if (argument.getSort() == Type.OBJECT) {
                    return uniqueStatelessImplementation(
                        argument.getInternalName(),
                        classesByName);
                }
            }
        }
        if (index + 2 >= instructions.size()
            || !(direct instanceof VarInsnNode store)
            || store.getOpcode() != Opcodes.ASTORE) {
            return null;
        }

        for (int useIndex = index + 1;
            useIndex < Math.min(instructions.size(), index + 20);
            useIndex++) {
            AbstractInsnNode use = instructions.get(useIndex);
            if (!(use instanceof VarInsnNode load)
                || load.getOpcode() != Opcodes.ALOAD
                || load.var != store.var) {
                continue;
            }
            for (int receiverIndex = useIndex + 1;
                receiverIndex <
                    Math.min(instructions.size(), useIndex + 10);
                receiverIndex++) {
                AbstractInsnNode receiverUse =
                    instructions.get(receiverIndex);
                if (receiverUse instanceof FieldInsnNode field
                    && field.getOpcode() == Opcodes.PUTFIELD
                    && availableTypes.contains(field.owner)) {
                    return field.owner;
                }
                if (receiverUse instanceof FieldInsnNode field
                    && field.getOpcode() == Opcodes.PUTSTATIC) {
                    String target = fieldType(
                        field,
                        availableTypes);
                    if (target != null) {
                        return target;
                    }
                }
                if (receiverUse instanceof TypeInsnNode cast
                    && cast.getOpcode() == Opcodes.CHECKCAST
                    && availableTypes.contains(cast.desc)) {
                    return cast.desc;
                }
                if (receiverUse instanceof VarInsnNode otherLoad
                    && otherLoad.getOpcode() == Opcodes.ALOAD
                    && otherLoad.var == store.var) {
                    break;
                }
            }
        }
        return null;
    }

    private static String uniqueStatelessImplementation(
        String expectedType,
        Map<String, ClassNode> classesByName) {
        String result = null;
        for (ClassNode candidate : classesByName.values()) {
            if ((candidate.access
                    & (Opcodes.ACC_ABSTRACT
                        | Opcodes.ACC_INTERFACE)) != 0
                || hasInstanceStateOrConstructor(candidate)
                || !isAssignableTo(
                    candidate,
                    expectedType,
                    classesByName,
                    new HashSet<>())) {
                continue;
            }
            if (result != null) {
                return null;
            }
            result = candidate.name;
        }
        return result;
    }

    private static boolean hasInstanceStateOrConstructor(
        ClassNode classNode) {
        return classNode.fields.stream().anyMatch(
                   field ->
                       (field.access & Opcodes.ACC_STATIC) == 0)
            || classNode.methods.stream().anyMatch(
                method -> "<init>".equals(method.name));
    }

    private static boolean isAssignableTo(
        ClassNode candidate,
        String expectedType,
        Map<String, ClassNode> classesByName,
        Set<String> visited) {
        if (!visited.add(candidate.name)) {
            return false;
        }
        if (expectedType.equals(candidate.superName)
            || candidate.interfaces.contains(expectedType)) {
            return true;
        }
        if (candidate.superName != null) {
            ClassNode parent = classesByName.get(
                candidate.superName);
            if (parent != null
                && isAssignableTo(
                    parent,
                    expectedType,
                    classesByName,
                    visited)) {
                return true;
            }
        }
        for (String interfaceName : candidate.interfaces) {
            ClassNode interfaceNode = classesByName.get(
                interfaceName);
            if (interfaceNode != null
                && isAssignableTo(
                    interfaceNode,
                    expectedType,
                    classesByName,
                    visited)) {
                return true;
            }
        }
        return false;
    }

    private static void addConstructorTarget(
        Map<String, Set<String>> constructorTargets,
        String className,
        String descriptor) {
        constructorTargets
            .computeIfAbsent(
                className,
                ignored -> new HashSet<>())
            .add(descriptor);
    }

    private static boolean isFieldWrite(
        FieldInsnNode field) {
        return field.getOpcode() == Opcodes.PUTFIELD
            || field.getOpcode() == Opcodes.PUTSTATIC;
    }

    private static String fieldType(
        FieldInsnNode field,
        Set<String> availableTypes) {
        Type type = Type.getType(field.desc);
        if (type.getSort() != Type.OBJECT) {
            return null;
        }
        String target = type.getInternalName();
        return !"java/lang/Object".equals(target)
            && availableTypes.contains(target)
                ? target
                : null;
    }

    private static List<AbstractInsnNode> realInstructions(
        MethodNode method) {
        List<AbstractInsnNode> result = new ArrayList<>();
        for (AbstractInsnNode instruction :
            method.instructions.toArray()) {
            if (instruction.getOpcode() >= 0) {
                result.add(instruction);
            }
        }
        return result;
    }

    private static void ensureForwardingConstructor(
        ClassNode classNode,
        String descriptor) {
        boolean exists = classNode.methods.stream().anyMatch(
            method ->
                "<init>".equals(method.name) &&
                descriptor.equals(method.desc));
        if (exists) {
            return;
        }
        MethodNode constructor = new MethodNode(
            "java/lang/Enum".equals(classNode.superName)
                ? Opcodes.ACC_PRIVATE
                : Opcodes.ACC_PUBLIC,
            "<init>",
            descriptor,
            null,
            null);
        constructor.instructions.add(
            new VarInsnNode(Opcodes.ALOAD, 0));
        int local = 1;
        for (Type argument :
            Type.getArgumentTypes(descriptor)) {
            constructor.instructions.add(
                new VarInsnNode(
                    argument.getOpcode(Opcodes.ILOAD),
                    local));
            local += argument.getSize();
        }
        constructor.instructions.add(
            new MethodInsnNode(
                Opcodes.INVOKESPECIAL,
                classNode.superName,
                "<init>",
                descriptor,
                false));
        constructor.instructions.add(
            new InsnNode(Opcodes.RETURN));
        constructor.maxStack = local;
        constructor.maxLocals = local;
        classNode.methods.add(constructor);
    }

    private static void writeClasses(
        Map<Path, ClassNode> classes) throws IOException {
        for (Map.Entry<Path, ClassNode> entry :
            classes.entrySet()) {
            ClassWriter writer = new ClassWriter(0);
            entry.getValue().accept(writer);
            Files.write(entry.getKey(), writer.toByteArray());
        }
    }
}
